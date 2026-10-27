using UnityEngine;
using UnityEditor;
using System.Net;
using System;
using System.Net.Http;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using System.Text;
using System.IO;
using System.Collections.Generic;
using System.IO.Compression;
using UnityEditor.SceneManagement;
using System.Linq;
using UnityEditor.Animations;
using UnityEngine.Rendering;

public enum RenderPipeline
{
	Unsupported,
	BuiltIn,
	URP,
	HDRP
}

public class MeshyBridgeWindow : EditorWindow
{
	static string _tempCachePath;

	static Thread serverThread;
	static Thread guardThread;
	static bool _serverStop;
	static TcpListener listener;

	static readonly Queue<MeshTransfer> importQueue = new();

	public static bool IsRunning { get; set; }

	GUIContent runButtonContent;
	GUIContent stopButtonContent;

	static bool _standOnGround = true;
	[Serializable]
	public class MeshTransfer
	{
		public string file_format;
		public string path;
		public string name;
		public int frameRate;
	}

	[MenuItem("Meshy/Bridge")]
	public static void ShowWindow()
	{
		MeshyBridgeWindow window = GetWindow<MeshyBridgeWindow>("Meshy Bridge");
		window.minSize = new(250, 120);
		window.maxSize = new(400, 170);
	}

	void OnEnable()
	{
		runButtonContent = new("Run Bridge");
		stopButtonContent = new("Bridge ON");

		_tempCachePath = Application.temporaryCachePath;
		EditorApplication.update += Update;
		StartServer();
	}

	void OnDisable()
	{
		EditorApplication.update -= Update;
		StopServer(true);
	}

	void OnGUI()
	{
		EditorGUILayout.BeginVertical();
		GUILayout.Space(10);
		GUIStyle buttonStyle = new(GUI.skin.button) { fontSize = 14, fontStyle = FontStyle.Bold, fixedHeight = 40 };
		Color originalColor = GUI.backgroundColor;
		if (IsRunning) GUI.backgroundColor = new Color(0.4f, 0.6f, 1.0f);
		GUIContent currentContent = IsRunning ? stopButtonContent : runButtonContent;
		if (GUILayout.Button(currentContent, buttonStyle)) ToggleBridgeState();
		GUI.backgroundColor = originalColor; 
		GUILayout.Space(5);
		_standOnGround = EditorGUILayout.Toggle(new GUIContent("Stand on Ground", "If enabled, imported models will be placed on the Y=0 plane."), _standOnGround);
		EditorGUILayout.EndVertical();
		Repaint();
	}

	void ToggleBridgeState()
	{
		if (IsRunning) StopServer();
		else StartServer();
	}

	public static void StartServer()
	{
		if (IsRunning) return;

		Debug.Log("[Meshy Bridge] Starting server");
		try
		{
			_serverStop = false;
			serverThread = new Thread(RunServer) { IsBackground = true };
			serverThread.Start();
		}
		catch (Exception e)
		{
			Debug.LogError($"[Meshy Bridge] Error starting server: {e.Message}\n{e.StackTrace}");
		}
	}

	static void GuardJob()
	{
		while (!_serverStop)
			Thread.Sleep(200);

		listener?.Stop();
		Debug.Log("[Meshy Bridge] Guard thread shutting down server");
	}

	static void RunServer()
	{
		try
		{
			listener = new TcpListener(IPAddress.Any, 5326);
			listener.Server.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
			listener.Start();
			IsRunning = true;

			guardThread = new Thread(GuardJob) { IsBackground = true };
			guardThread.Start();

			Debug.Log("[Meshy Bridge] Listening on port 5326");

			while (!_serverStop)
			{
				if (listener.Pending())
				{
					// One pool thread per connection, so a slow download never blocks /status
					TcpClient client = listener.AcceptTcpClient();
					ThreadPool.QueueUserWorkItem(_ => HandleClient(client));
				}
				Thread.Sleep(100);
			}
		}
		catch (SocketException e) when (e.SocketErrorCode == SocketError.Interrupted)
		{
			// Listener was stopped, which is expected.
		}
		catch (Exception e)
		{
			Debug.LogError($"[Meshy Bridge] Server run error: {e.Message}\n{e.StackTrace}");
		}
		finally
		{
			IsRunning = false;
			listener?.Stop();
			Debug.Log("[Meshy Bridge] Server stopped");
		}
	}

	public static void StopServer(bool blocking = false)
	{
		if (_serverStop) return;

		Debug.Log("[Meshy Bridge] Stopping server");
		_serverStop = true;

		if (!blocking) return;

		serverThread?.Join();
		guardThread?.Join();
	}

	const int DownloadTimeoutSeconds = 90;
	const int RequestReadTimeoutMs = 10000;
	const int MaxHeaderBytes = 64 * 1024;
	const int MaxBodyBytes = 1024 * 1024;

	// Carries the HTTP status and a client-safe message (no URLs, no local paths)
	class BridgeException : Exception
	{
		public readonly int Status;
		public BridgeException(int status, string message) : base(message) { Status = status; }
	}

	static void HandleClient(TcpClient client)
	{
		using (client)
		using (NetworkStream stream = client.GetStream())
			ProcessClientRequest(stream);
	}

	// Reads the headers, then exactly Content-Length body bytes. Returns false if the peer sent nothing.
	static bool TryReadRequest(NetworkStream stream, out string[] requestLines, out string body)
	{
		requestLines = null;
		body = "";
		stream.ReadTimeout = RequestReadTimeoutMs;

		byte[] chunk = new byte[1024 * 16];
		using MemoryStream received = new();
		int bodyStart = -1;
		int contentLength = 0;
		while (true)
		{
			if (bodyStart < 0)
			{
				byte[] buf = received.GetBuffer();
				for (int i = 3; i < received.Length && bodyStart < 0; i++)
					if (buf[i - 3] == '\r' && buf[i - 2] == '\n' && buf[i - 1] == '\r' && buf[i] == '\n')
						bodyStart = i + 1;

				if (bodyStart >= 0)
				{
					requestLines = Encoding.UTF8.GetString(buf, 0, bodyStart).Split(new[] { "\r\n" }, StringSplitOptions.None);
					int.TryParse(GetHeaderValue(requestLines, "Content-Length"), out contentLength);
					if (contentLength < 0 || contentLength > MaxBodyBytes)
						throw new BridgeException(400, "request body too large");
				}
				else if (received.Length > MaxHeaderBytes)
					throw new BridgeException(400, "request headers too large");
			}

			if (bodyStart >= 0 && received.Length >= bodyStart + contentLength)
				break;

			int n;
			try { n = stream.Read(chunk, 0, chunk.Length); }
			catch (IOException) { n = -1; }
			if (n <= 0)
			{
				if (received.Length == 0) return false;
				throw new BridgeException(400, n < 0 ? "request timed out" : "incomplete request");
			}
			received.Write(chunk, 0, n);
		}

		body = Encoding.UTF8.GetString(received.GetBuffer(), bodyStart, contentLength);
		return true;
	}

	static void ProcessClientRequest(NetworkStream stream)
	{
		string origin = "";
		try
		{
			Debug.Log("[Meshy Bridge] Processing request");
			if (!TryReadRequest(stream, out string[] requestLines, out string body))
				return;
			Debug.Log($"[Meshy Bridge] Received request:\n{string.Join("\r\n", requestLines)}\n{body}");

			string[] requestParts = requestLines[0].Split(' ');
			if (requestParts.Length < 2)
			{
				Debug.LogWarning("[Meshy Bridge] Invalid request format: " + requestLines[0]);
				throw new BridgeException(400, "Invalid request format");
			}

			string method = requestParts[0];
			string path = requestParts[1];
			origin = GetHeaderValue(requestLines, "Origin");

			if (method == "OPTIONS")
			{
				SendOptionsResponse(stream, origin);
				return;
			}

			if (method == "GET" && (path == "/status" || path == "/ping"))
			{
				SendStatusResponse(stream, origin);
				return;
			}

			if (method == "POST" && path == "/import")
			{
				// A cross-site "simple" POST skips preflight, so refuse foreign origins here; no Origin means a local tool
				if (!string.IsNullOrEmpty(origin) && !IsAllowedOrigin(origin))
					throw new BridgeException(403, "origin not allowed");
				ProcessImportRequest(stream, body, origin);
				return;
			}

			SendNotFoundResponse(stream, origin);
		}
		catch (BridgeException e)
		{
			Debug.LogWarning($"[Meshy Bridge] Request rejected ({e.Status}): {e.Message}");
			SendErrorResponse(stream, origin, e.Status, e.Message);
		}
		catch (Exception e)
		{
			Debug.LogError($"[Meshy Bridge] Error processing request: {e.GetType().Name} - {e.Message}\n{e.StackTrace}");
			SendErrorResponse(stream, origin, 500, "internal error");
		}
	}

	[Serializable]
	class ImportResponseData
	{
		public string status;
		public string message;
	}

	static void ProcessImportRequest(NetworkStream stream, string jsonBody, string origin)
	{
		jsonBody = jsonBody.Trim();
		if (!jsonBody.StartsWith("{") || !jsonBody.EndsWith("}"))
			throw new BridgeException(400, "request body must be a JSON object");

		ImportRequestData data;
		try { data = JsonUtility.FromJson<ImportRequestData>(jsonBody); }
		catch (Exception) { throw new BridgeException(400, "invalid JSON"); }

		if (string.IsNullOrEmpty(data?.url))
			throw new BridgeException(400, "missing url");
		if (!Uri.TryCreate(data.url, UriKind.Absolute, out Uri uri)
			|| (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
			throw new BridgeException(400, "url must be http(s)");

		string fileName = $"bridge_model_{DateTime.Now:yyyyMMdd_HHmmss}_{Guid.NewGuid().ToString("N")[..8]}";
		string filePath = Path.Combine(Path.GetTempPath(), "Meshy", fileName);

		Directory.CreateDirectory(Path.GetDirectoryName(filePath));

		if (File.Exists(filePath))
		{
			File.Delete(filePath);
			Debug.Log($"[Meshy Bridge] Deleted existing file: {filePath}");
		}

		Debug.Log($"[Meshy Bridge] Downloading: {data.url}");
		string fileExtension;
		try
		{
			DownloadFile(uri, filePath);
			fileExtension = SniffModelExtension(filePath);
		}
		catch
		{
			CleanupTempFile(filePath);
			throw;
		}

		string finalPath = filePath + fileExtension;

		if (File.Exists(finalPath))
		{
			File.Delete(finalPath);
			Debug.Log($"[Meshy Bridge] Deleted existing target file: {finalPath}");
		}

		File.Move(filePath, finalPath);
		filePath = finalPath;
		Debug.Log($"[Meshy Bridge] File saved: {filePath}");

		lock (importQueue)
		{
			importQueue.Enqueue(
				new()
				{
					file_format = fileExtension.Substring(1),
					path = filePath,
					name = data.name ?? "",
					frameRate = data.frameRate
				});
		}

		SendJsonResponse(stream, origin, 200, "ok", "File queued for import");
		Debug.Log("[Meshy Bridge] Response sent");
	}

	static void DownloadFile(Uri uri, string filePath)
	{
		HttpResponseMessage response;
		try
		{
			// Buffered read, so Timeout covers the whole transfer
			using HttpClient client = new() { Timeout = TimeSpan.FromSeconds(DownloadTimeoutSeconds) };
			response = client.GetAsync(uri).GetAwaiter().GetResult();
		}
		catch (TaskCanceledException)
		{
			throw new BridgeException(502, "download failed: timeout");
		}
		catch (Exception e)
		{
			Debug.LogWarning($"[Meshy Bridge] Download error: {e.GetType().Name} - {e.Message}");
			throw new BridgeException(502, "download failed: network error");
		}

		using (response)
		{
			if (!response.IsSuccessStatusCode)
				throw new BridgeException(502, $"download failed: HTTP {(int)response.StatusCode}");

			using FileStream fs = File.Create(filePath);
			response.Content.CopyToAsync(fs).GetAwaiter().GetResult();
		}
	}

	// Maps the file's magic bytes to the extension ProcessMeshTransfer expects
	static string SniffModelExtension(string filePath)
	{
		byte[] header = new byte[32];
		int read;
		using (FileStream fs = new(filePath, FileMode.Open, FileAccess.Read))
			read = fs.Read(header, 0, header.Length);

		string head = Encoding.ASCII.GetString(header, 0, read);
		if (read >= 4 && header[0] == 'P' && header[1] == 'K' && header[2] == 0x03 && header[3] == 0x04)
			return ".zip";
		if (head.StartsWith("glTF", StringComparison.Ordinal))
			return ".glb";
		if (head.StartsWith("Kaydara FBX Binary", StringComparison.Ordinal) || head.StartsWith("; FBX", StringComparison.Ordinal))
			return ".fbx";

		throw new BridgeException(415, "unsupported file format");
	}

	[Serializable]
	class ImportRequestData
	{
		public string url;
		public string format;
		public string name;
		public int frameRate = 30;
	}

	static bool IsAllowedOrigin(string origin)
	{
		if (string.IsNullOrEmpty(origin)) return false;

		// Exactly https://<host> (no port, path or userinfo), host is meshy.ai / meshy.cn or a subdomain of them
		if (!Uri.TryCreate(origin, UriKind.Absolute, out Uri uri)
			|| uri.Scheme != Uri.UriSchemeHttps
			|| uri.UserInfo.Length > 0
			|| !uri.IsDefaultPort
			|| !string.Equals(uri.GetLeftPart(UriPartial.Authority), origin, StringComparison.OrdinalIgnoreCase))
			return false;

		string host = uri.Host;
		return host == "meshy.ai" || host.EndsWith(".meshy.ai", StringComparison.Ordinal)
			|| host == "meshy.cn" || host.EndsWith(".meshy.cn", StringComparison.Ordinal);
	}

	static string GetAllowedOrigin(string origin) =>
		IsAllowedOrigin(origin) ? origin : "https://www.meshy.ai";

	static void SendOptionsResponse(NetworkStream stream, string origin)
	{
		string response = $"HTTP/1.1 200 OK\r\n" +
		                  $"Access-Control-Allow-Origin: {GetAllowedOrigin(origin)}\r\n" +
		                  "Access-Control-Allow-Methods: POST, GET, OPTIONS\r\n" +
		                  "Access-Control-Allow-Headers: *\r\n" +
		                  "Access-Control-Max-Age: 86400\r\n\r\n";
		stream.Write(Encoding.UTF8.GetBytes(response), 0, response.Length);
	}

	[Serializable]
	class StatusResponseData
	{
		public string status = "ok";
		public string dcc = "unity";
		public string version;
	}

	static void SendStatusResponse(NetworkStream stream, string origin)
	{
		StatusResponseData responseData = new()
		{
			dcc = "unity",
			status = "ok",
			version = Application.unityVersion
		};

		string jsonResponse = JsonUtility.ToJson(responseData);

		string response = string.Join(
			"\r\n",
			"HTTP/1.1 200 OK",
			$"Access-Control-Allow-Origin: {GetAllowedOrigin(origin)}",
			"Content-Type: application/json; charset=utf-8",
			"Connection: close",
			$"Content-Length: {jsonResponse.Length}",
			"",
			jsonResponse);

		byte[] responseBytes = Encoding.UTF8.GetBytes(response);
		stream.Write(responseBytes, 0, responseBytes.Length);
		stream.Flush();

		Debug.Log($"[Meshy Bridge] Status response sent: {jsonResponse}");
	}

	static void SendNotFoundResponse(NetworkStream stream, string origin) =>
		SendErrorResponse(stream, origin, 404, "path not found");

	static void SendErrorResponse(NetworkStream stream, string origin, int code, string message)
	{
		try
		{
			SendJsonResponse(stream, origin, code, "error", message);
		}
		catch (Exception e)
		{
			Debug.LogError($"[Meshy Bridge] Failed to send error response: {e.Message}");
		}
	}

	static void SendJsonResponse(NetworkStream stream, string origin, int code, string status, string message)
	{
		string reason = code switch
		{
			200 => "OK",
			400 => "Bad Request",
			403 => "Forbidden",
			404 => "Not Found",
			415 => "Unsupported Media Type",
			502 => "Bad Gateway",
			_ => "Internal Server Error"
		};
		byte[] body = Encoding.UTF8.GetBytes(JsonUtility.ToJson(new ImportResponseData { status = status, message = message }));
		string head = string.Join(
			"\r\n",
			$"HTTP/1.1 {code} {reason}",
			$"Access-Control-Allow-Origin: {GetAllowedOrigin(origin)}",
			"Content-Type: application/json; charset=utf-8",
			"Connection: close",
			$"Content-Length: {body.Length}",
			"",
			"");

		byte[] headBytes = Encoding.UTF8.GetBytes(head);
		stream.Write(headBytes, 0, headBytes.Length);
		stream.Write(body, 0, body.Length);
		stream.Flush();
	}

	static string GetHeaderValue(string[] headers, string headerName)
	{
		foreach (string header in headers)
			if (header.StartsWith(headerName + ":", StringComparison.OrdinalIgnoreCase))
				return header.Substring(headerName.Length + 1).Trim();
		return "";
	}

	static void Update()
	{
		while (true)
		{
			MeshTransfer transfer;
			// Don't hold the lock while importing, so /import can keep enqueueing
			lock (importQueue)
			{
				if (importQueue.Count == 0) return;
				transfer = importQueue.Dequeue();
			}
			ProcessMeshTransfer(transfer);
		}
	}

	static void ProcessMeshTransfer(MeshTransfer transfer)
	{
		try
		{
			string fileExtension = Path.GetExtension(transfer.path)?.ToLower();
			switch (fileExtension)
			{
				case ".glb":
					ImportModelWithMaterial(transfer);
					break;
				case ".zip":
					ProcessZipFile(transfer);
					break;
				case ".fbx":
					ImportFBXWithTextures(transfer);
					break;
			}
		}
		catch (Exception e)
		{
			Debug.LogError($"[Meshy Bridge] Error processing mesh: {e.Message}");
		}
		finally
		{
			CleanupTempFile(transfer.path);
		}
	}

	static void ImportModelWithMaterial(MeshTransfer transfer)
	{
		try
		{
			string importDir = "Assets/MeshyImports";
			if (!Directory.Exists(importDir))
			{
				Directory.CreateDirectory(importDir);
				AssetDatabase.Refresh();
			}

			string modelName = string.IsNullOrEmpty(transfer.name) ? "Meshy_Model" : transfer.name;
			modelName = string.Join("_", modelName.Split(Path.GetInvalidFileNameChars()));

			string extension = Path.GetExtension(transfer.path);
			if (string.IsNullOrEmpty(extension))
				extension = $".{transfer.file_format}";

			string uniqueFileName = $"{modelName}_{DateTime.Now:yyyyMMdd_HHmmss}{extension}";
			string relativePath = Path.Combine(importDir, uniqueFileName);

			if (!File.Exists(transfer.path))
			{
				Debug.LogError($"[Meshy Bridge] Source file not found: {transfer.path}");
				return;
			}

			File.Copy(transfer.path, relativePath, true);

			AssetDatabase.ImportAsset(relativePath, ImportAssetOptions.ForceUpdate);

			if (AssetImporter.GetAtPath(relativePath) is ModelImporter importer)
			{
				importer.animationType = ModelImporterAnimationType.Generic;
				importer.importAnimation = true;

				if (transfer.file_format.ToLower() == "glb")
				{
					importer.SaveAndReimport();

					AnimationClip[] clips = AssetDatabase.LoadAllAssetsAtPath(relativePath).OfType<AnimationClip>().ToArray();
					if (clips.Length > 1)
					{
						Debug.Log($"[Meshy Bridge] Found {clips.Length} animation clips in GLB file");
						foreach (AnimationClip clip in clips)
							Debug.Log($"[Meshy Bridge] Animation clip: {clip.name}");
					}
				}
			}

			GameObject importedObject = AssetDatabase.LoadAssetAtPath<GameObject>(relativePath);
			if (importedObject == null) return;

			importedObject.name = uniqueFileName;

			AddDefaultMaterial(importedObject);

			EditorUtility.SetDirty(importedObject);
			AssetDatabase.SaveAssets();

			EditorApplication.delayCall += () =>
			{
				if (PrefabUtility.InstantiatePrefab(importedObject) is not GameObject sceneObject) return;

				sceneObject.transform.position = Vector3.zero;
				sceneObject.transform.localScale = Vector3.one;

				if (_standOnGround)
				{
					// Keep the prefab's original rotation and adjust Y position
					Renderer[] renderers = sceneObject.GetComponentsInChildren<Renderer>();
					if (renderers.Length > 0)
					{
						Bounds bounds = renderers[0].bounds;
						for (int i = 1; i < renderers.Length; i++)
						{
							bounds.Encapsulate(renderers[i].bounds);
						}

						if (bounds.size != Vector3.zero)
						{
							sceneObject.transform.position = new Vector3(0, -bounds.min.y, 0);
						}
					}
				}
				else
				{
					// Reset rotation to identity (original model orientation)
					sceneObject.transform.rotation = Quaternion.identity;
				}

				// Ensure Animator component exists for animation clips
				if (sceneObject.GetComponent<Animator>() == null)
					sceneObject.AddComponent<Animator>();

				CreateAnimatorControllerForMultipleClips(sceneObject, relativePath);

				Selection.activeGameObject = sceneObject;
				EditorSceneManager.MarkSceneDirty(sceneObject.scene);

				Debug.Log($"[Meshy Bridge] Model successfully added to scene: {sceneObject.name}");
			};
			Debug.Log($"[Meshy Bridge] Model imported successfully: {relativePath}, Name: {uniqueFileName}");
		}
		catch (Exception e)
		{
			Debug.LogError($"[Meshy Bridge] Model import failed: {e.Message}\n{e.StackTrace}");
		}
	}

	static void AddDefaultMaterial(GameObject obj)
	{
		Renderer renderer = obj.GetComponent<Renderer>();
		if (renderer == null || renderer.sharedMaterials.Any(m => m != null)) return;
	
		RenderPipeline pipeline = GetActiveRenderPipeline();
		Shader shader;
		switch (pipeline)
		{
			case RenderPipeline.URP:
				shader = Shader.Find("Universal Render Pipeline/Lit");
				break;
			case RenderPipeline.HDRP:
				shader = Shader.Find("HDRP/Lit");
				break;
			default:
				shader = Shader.Find("Standard");
				break;
		}
	
		if (shader == null)
		{
			Debug.LogWarning("[Meshy Bridge] Could not find a default shader for the current render pipeline. Falling back to Standard.");
			shader = Shader.Find("Standard");
		}
	
		Material material = new(shader) { name = "Meshy_Material" };
	
		if (renderer.sharedMaterials.Length == 0)
			renderer.sharedMaterial = material;
		else
		{
			Material[] materials = renderer.sharedMaterials;
			for (int i = 0; i < materials.Length; i++)
				if (materials[i] == null)
					materials[i] = material;
			renderer.sharedMaterials = materials;
		}
	}

	static void ProcessZipFile(MeshTransfer transfer)
	{
		string extractPath = Path.Combine(_tempCachePath, "extracted");
		
		// Clean up existing extraction directory if it exists
		if (Directory.Exists(extractPath))
			Directory.Delete(extractPath, true);
			
		ZipFile.ExtractToDirectory(transfer.path, extractPath);

		foreach (string file in Directory.GetFiles(extractPath, "*.glb", SearchOption.AllDirectories))
		{
			ImportModelWithMaterial(
				new()
				{
					file_format = "glb",
					path = file,
					name = transfer.name
				});
		}

		foreach (string file in Directory.GetFiles(extractPath, "*.fbx", SearchOption.AllDirectories))
		{
			ImportFBXWithTextures(
				new()
				{
					file_format = "fbx",
					path = file,
					name = transfer.name
				});
		}

		Directory.Delete(extractPath, true);
	}

	static void ImportFBXWithTextures(MeshTransfer transfer)
	{
		try
		{
			string importDir = "Assets/MeshyImports";
			if (!Directory.Exists(importDir))
			{
				Directory.CreateDirectory(importDir);
				AssetDatabase.Refresh();
			}

			string modelName = string.IsNullOrEmpty(transfer.name) ? "Meshy_Model" : transfer.name;
			modelName = string.Join("_", modelName.Split(Path.GetInvalidFileNameChars()));

			string modelFolderName = $"{modelName}_{DateTime.Now:yyyyMMdd_HHmmss}";
			string modelDir = Path.Combine(importDir, modelFolderName);
			Directory.CreateDirectory(modelDir);

			string fbxFileName = Path.GetFileName(transfer.path);
			string fbxRelativePath = Path.Combine(modelDir, fbxFileName);

			if (!File.Exists(transfer.path))
			{
				Debug.LogError($"[Meshy Bridge] Source FBX file not found: {transfer.path}");
				return;
			}

			File.Copy(transfer.path, fbxRelativePath, true);
			AssetDatabase.Refresh();

			AssetDatabase.ImportAsset(fbxRelativePath, ImportAssetOptions.ForceUpdate);

			GameObject importedObject = AssetDatabase.LoadAssetAtPath<GameObject>(fbxRelativePath);
			if (!importedObject) return;
			importedObject.name = modelName;

			string sourceDir = Path.GetDirectoryName(transfer.path);

			// The export pipeline ships role-named maps next to the FBX. Identify
			// them straight from the source (zip) dir and let BuildMeshyMaterials
			// copy ONLY the channels it uses (as meshy_* assets). We deliberately
			// do NOT pre-copy every loose texture into the project — that left a
			// duplicate, original-named copy of each map beside the meshy_* one.
			MeshyTextureSet meshyTextures = FindMeshyTextures(sourceDir);
			if (meshyTextures.HasAny())
				BuildMeshyMaterials(importedObject, modelDir, meshyTextures);
			else
			{
				// No per-channel PNG maps shipped with this FBX. Fall back to the
				// FBX-embedded textures (may be lossy, no metallic/roughness/emission
				// split); the heuristic matcher needs them copied into the project.
				Debug.LogWarning($"[Meshy Bridge] No per-channel PNG maps found next to {fbxFileName}; falling back to FBX-embedded textures (may be lossy, no metallic/roughness/emission split).");
				ImportTextureFiles(sourceDir, modelDir);
				FixMaterialTextureReferences(importedObject, modelDir);
			}

			EditorUtility.SetDirty(importedObject);
			AssetDatabase.SaveAssets();

			EditorApplication.delayCall += () =>
			{
				if (PrefabUtility.InstantiatePrefab(importedObject) is not GameObject sceneObject) return;

				sceneObject.transform.position = Vector3.zero;
				sceneObject.transform.localScale = Vector3.one;

				if (_standOnGround)
				{
					// Keep the prefab's original rotation (FBX coordinate system correction)
					// and adjust Y position so the model stands on the ground (Y=0)
					Renderer[] renderers = sceneObject.GetComponentsInChildren<Renderer>();
					if (renderers.Length > 0)
					{
						Bounds bounds = renderers[0].bounds;
						for (int i = 1; i < renderers.Length; i++)
						{
							bounds.Encapsulate(renderers[i].bounds);
						}

						if (bounds.size != Vector3.zero)
						{
							sceneObject.transform.position = new Vector3(0, -bounds.min.y, 0);
						}
					}
				}
				else
				{
					// Reset rotation to identity (original model orientation)
					sceneObject.transform.rotation = Quaternion.identity;
				}

				Selection.activeGameObject = sceneObject;
				EditorSceneManager.MarkSceneDirty(sceneObject.scene);

				Debug.Log($"[Meshy Bridge] FBX model successfully added to scene: {sceneObject.name}");
			};

			Debug.Log($"[Meshy Bridge] FBX model imported successfully: {fbxRelativePath}");
		}
		catch (Exception e)
		{
			Debug.LogError($"[Meshy Bridge] FBX import failed: {e.Message}\n{e.StackTrace}");
		}
	}

	static void ImportTextureFiles(string sourceDir, string targetDir)
	{
		string[] textureExtensions = { "*.jpg", "*.jpeg", "*.png", "*.tga", "*.bmp", "*.tiff", "*.tif", "*.exr", "*.hdr" };
		const string normalMapKeyword = "texture_normal";

		var allTextureFiles = textureExtensions.SelectMany(ext => Directory.GetFiles(sourceDir, ext, SearchOption.AllDirectories));

		foreach (string sourcePath in allTextureFiles)
		{
			string relativePath = Path.GetRelativePath(sourceDir, sourcePath);
			string targetPath = Path.Combine(targetDir, relativePath);

			Directory.CreateDirectory(Path.GetDirectoryName(targetPath));
			File.Copy(sourcePath, targetPath, true);

			AssetDatabase.ImportAsset(targetPath, ImportAssetOptions.ForceUpdate);
			if (AssetImporter.GetAtPath(targetPath) is not TextureImporter textureImporter) continue;

			string lowerFileName = Path.GetFileName(targetPath).ToLower();
			if (lowerFileName.Contains(normalMapKeyword))
			{
				if (textureImporter.textureType != TextureImporterType.NormalMap)
				{
					textureImporter.textureType = TextureImporterType.NormalMap;
					textureImporter.SaveAndReimport();
					Debug.Log($"[Meshy Bridge] Set texture type to Normal Map for: {Path.GetFileName(targetPath)}");
				}
			}
			else
			{
				Debug.Log($"[Meshy Bridge] Copied texture file: {Path.GetFileName(targetPath)}");
			}
		}
	}

	static void FixMaterialTextureReferences(GameObject fbxObject, string modelDir)
	{
		RenderPipeline pipeline = GetActiveRenderPipeline();
		Renderer[] renderers = fbxObject.GetComponentsInChildren<Renderer>();
		
		foreach (Renderer renderer in renderers)
		{
			Material[] sharedMaterials = renderer.sharedMaterials;
			Material[] newMaterials = new Material[sharedMaterials.Length];
			for (int i = 0; i < sharedMaterials.Length; i++)
			{
				Material originalMaterial = sharedMaterials[i];
				if (originalMaterial == null) 
				{
					newMaterials[i] = null;
					continue;
				}
				Material material = new(originalMaterial);
				Shader newShader;

				switch (pipeline)
				{
					case RenderPipeline.URP:
						newShader = Shader.Find("Universal Render Pipeline/Lit");
						break;
					case RenderPipeline.HDRP:
						newShader = Shader.Find("HDRP/Lit");
						break;
					default:
						newShader = Shader.Find("Standard");
						break;
				}
				if (newShader != null) material.shader = newShader;

				string shaderName = material.shader.name;
				bool isURP = shaderName.Contains("Universal Render Pipeline");
				bool isHDRP = shaderName.Contains("HDRP");
				string albedoPropertyName = isURP ? "_BaseMap" : isHDRP ? "_BaseColorMap" : "_MainTex";
				if (material.HasProperty(albedoPropertyName) && material.GetTexture(albedoPropertyName) == null)
				{
					Texture2D albedoTexture = FindTextureInDirectory(modelDir, originalMaterial.name) ?? FindTextureInDirectory(modelDir, "albedo") ?? FindTextureInDirectory(modelDir, "diffuse") ?? FindTextureInDirectory(modelDir, "basecolor") ?? FindTextureInDirectory(modelDir, "base_color") ?? FindFirstTextureInDirectory(modelDir);
					if (albedoTexture != null)
					{
						material.SetTexture(albedoPropertyName, albedoTexture);
						Debug.Log($"[Meshy Bridge] Set {albedoPropertyName} for material {material.name}: {albedoTexture.name}");
					}
				}
				if (isURP)
				{
					if (CheckAndAssignTexture(material, "_BumpMap", modelDir, "normal", "Normal"))
						material.SetFloat("_BumpScale", 0.5f);
					// Also fix existing normal map texture type if already assigned
					EnsureNormalMapTextureType(material, "_BumpMap");
					
					CheckAndAssignTexture(material, "_MetallicGlossMap", modelDir, "metallic", "Metallic");
					CheckAndAssignTexture(material, "_OcclusionMap", modelDir, "occlusion", "AO", "ambient_occlusion");
					CheckAndAssignTexture(material, "_EmissionMap", modelDir, "emission", "Emissive");

					if (material.GetTexture("_MetallicGlossMap") == null && material.HasProperty("_Smoothness"))
						material.SetFloat("_Smoothness", 0.5f);
				}
				else if (isHDRP)
				{
					CheckAndAssignTexture(material, "_NormalMap", modelDir, "normal", "Normal");
					// Also fix existing normal map texture type if already assigned
					EnsureNormalMapTextureType(material, "_NormalMap");
					
					CheckAndAssignTexture(material, "_EmissiveColorMap", modelDir, "emission", "Emissive");
				}
				else
				{
					CheckAndAssignTexture(material, "_BumpMap", modelDir, "normal", "Normal");
					// Also fix existing normal map texture type if already assigned
					EnsureNormalMapTextureType(material, "_BumpMap");
					
					CheckAndAssignTexture(material, "_MetallicGlossMap", modelDir, "metallic", "Metallic");
					CheckAndAssignTexture(material, "_OcclusionMap", modelDir, "occlusion", "AO", "ambient_occlusion");
					CheckAndAssignTexture(material, "_EmissionMap", modelDir, "emission", "Emissive");
					if (material.GetTexture("_MetallicGlossMap") == null && material.HasProperty("_Glossiness"))
						material.SetFloat("_Glossiness", 0.5f);
				}

				string materialPath = Path.Combine(modelDir, $"{material.name.Replace("(Instance)", "").Trim()}.mat");
				AssetDatabase.CreateAsset(material, materialPath);
				newMaterials[i] = AssetDatabase.LoadAssetAtPath<Material>(materialPath);
				Debug.Log($"[Meshy Bridge] Created and saved material asset at: {materialPath}");
			}
			renderer.sharedMaterials = newMaterials;
		}
	}

	// =====================================================================
	// Channel-correct PBR rebuild from the FBX zip's named textures.
	// The export pipeline ships the FBX alongside loose, role-named maps:
	// <stem>.png (base color) and <stem>_normal/_metallic/_roughness/_emission/
	// _ao.png. FBX can't carry metallic/roughness, so we identify the loose
	// maps by suffix and assign them to the correct material channels.
	// Building a FRESH material is also the white-material fix: copying the
	// FBX-imported material kept stale shader keywords (e.g. _NORMALMAP enabled
	// with a null _BumpMap).
	// =====================================================================

	const string MeshyBaseColorName = "meshy_basecolor";
	const string MeshyNormalName = "meshy_normal";
	const string MeshyEmissionName = "meshy_emission";
	const string MeshyOcclusionName = "meshy_occlusion";
	const string MeshyMetallicGlossName = "meshy_metallic_smoothness";
	const string MeshyMaskName = "meshy_mask";

	static readonly string[] MeshyTextureExtensions =
		{ ".png", ".jpg", ".jpeg", ".tga", ".bmp", ".tif", ".tiff", ".exr" };

	/// <summary>Local source paths of the model's per-channel textures (found in the import dir).</summary>
	class MeshyTextureSet
	{
		public string color;
		public string normal;
		public string metallic;
		public string roughness;
		public string emission;
		public string ao;

		public bool HasAny() =>
			!string.IsNullOrEmpty(color) || !string.IsNullOrEmpty(normal)
			|| !string.IsNullOrEmpty(metallic) || !string.IsNullOrEmpty(roughness)
			|| !string.IsNullOrEmpty(emission) || !string.IsNullOrEmpty(ao);
	}

	static bool HasChannelSuffix(string nameLower) =>
		nameLower.EndsWith("_normal") || nameLower.EndsWith("_normalmap")
		|| nameLower.EndsWith("_metallic") || nameLower.EndsWith("_metalness")
		|| nameLower.EndsWith("_roughness")
		|| nameLower.EndsWith("_emission") || nameLower.EndsWith("_emissive")
		|| nameLower.EndsWith("_ao") || nameLower.EndsWith("_occlusion") || nameLower.EndsWith("_ambient_occlusion")
		|| nameLower.EndsWith("_metallic_roughness") || nameLower.EndsWith("_orm")
		|| nameLower.EndsWith("_height") || nameLower.EndsWith("_displacement");

	// Identify the model's per-channel textures by filename suffix. The export
	// pipeline names them <stem>[_channel].<ext>; the base color is the map
	// with no recognized channel suffix.
	static MeshyTextureSet FindMeshyTextures(string modelDir)
	{
		var set = new MeshyTextureSet();
		string[] files = Directory.GetFiles(modelDir, "*.*", SearchOption.AllDirectories)
			.Where(f => MeshyTextureExtensions.Contains(Path.GetExtension(f).ToLower()))
			.ToArray();

		foreach (string file in files)
		{
			string n = Path.GetFileNameWithoutExtension(file).ToLower();
			if (n.EndsWith("_normal") || n.EndsWith("_normalmap")) set.normal ??= file;
			else if (n.EndsWith("_metallic") || n.EndsWith("_metalness")) set.metallic ??= file;
			else if (n.EndsWith("_roughness")) set.roughness ??= file;
			else if (n.EndsWith("_emission") || n.EndsWith("_emissive")) set.emission ??= file;
			else if (n.EndsWith("_ao") || n.EndsWith("_occlusion") || n.EndsWith("_ambient_occlusion")) set.ao ??= file;
		}

		// Packed metallic_roughness (glTF G=rough, B=metal): fall back to it for
		// both channels when separate maps are absent.
		string packed = files.FirstOrDefault(f =>
		{
			string n = Path.GetFileNameWithoutExtension(f).ToLower();
			return n.EndsWith("_metallic_roughness") || n.EndsWith("_orm");
		});
		if (packed != null)
		{
			set.metallic ??= packed;
			set.roughness ??= packed;
		}

		// Base color = the map with no recognized channel suffix.
		set.color = files.FirstOrDefault(f => !HasChannelSuffix(Path.GetFileNameWithoutExtension(f).ToLower()));
		return set;
	}

	static void BuildMeshyMaterials(GameObject fbxObject, string modelDir, MeshyTextureSet files)
	{
		RenderPipeline pipeline = GetActiveRenderPipeline();

		// Maps that bind 1:1 to a material slot.
		Texture2D baseColor = ImportRawAsAsset(files.color, modelDir, MeshyBaseColorName, isNormal: false, sRGB: true);
		Texture2D normal = ImportRawAsAsset(files.normal, modelDir, MeshyNormalName, isNormal: true, sRGB: false);
		Texture2D emission = ImportRawAsAsset(files.emission, modelDir, MeshyEmissionName, isNormal: false, sRGB: true);
		Texture2D occlusion = ImportRawAsAsset(files.ao, modelDir, MeshyOcclusionName, isNormal: false, sRGB: false);

		// metallic + roughness must be packed into the engine-specific layout.
		Texture2D metallicGloss = null; // URP / Built-in: RGB = metallic, A = smoothness (1 - roughness)
		Texture2D maskMap = null;       // HDRP: R = metallic, G = AO, B = detail, A = smoothness
		if (pipeline == RenderPipeline.HDRP)
			maskMap = ComposeHDRPMaskMap(files.metallic, files.roughness, files.ao, modelDir, MeshyMaskName);
		else
			metallicGloss = ComposeMetallicSmoothnessMap(files.metallic, files.roughness, modelDir, MeshyMetallicGlossName);

		Renderer[] renderers = fbxObject.GetComponentsInChildren<Renderer>();
		foreach (Renderer renderer in renderers)
		{
			Material[] shared = renderer.sharedMaterials;
			Material[] rebuilt = new Material[shared.Length];
			for (int i = 0; i < shared.Length; i++)
			{
				Material original = shared[i];
				if (original == null) { rebuilt[i] = null; continue; }

				Shader shader = pipeline switch
				{
					RenderPipeline.URP => Shader.Find("Universal Render Pipeline/Lit"),
					RenderPipeline.HDRP => Shader.Find("HDRP/Lit"),
					_ => Shader.Find("Standard"),
				};
				// Fresh material — never copy the FBX-imported one, so no stale keywords survive.
				Material mat = new(shader) { name = SanitizeMaterialName(original.name) };

				if (pipeline == RenderPipeline.URP)
					ConfigureUrpMaterial(mat, baseColor, normal, metallicGloss, occlusion, emission);
				else if (pipeline == RenderPipeline.HDRP)
					ConfigureHdrpMaterial(mat, baseColor, normal, maskMap, emission);
				else
					ConfigureBuiltinMaterial(mat, baseColor, normal, metallicGloss, occlusion, emission);

				string materialPath = Path.Combine(modelDir, $"{mat.name}.mat").Replace('\\', '/');
				AssetDatabase.CreateAsset(mat, materialPath);
				rebuilt[i] = AssetDatabase.LoadAssetAtPath<Material>(materialPath);
				Debug.Log($"[Meshy Bridge] Rebuilt PBR material from bridge textures: {materialPath}");
			}
			renderer.sharedMaterials = rebuilt;
		}
	}

	static string SanitizeMaterialName(string name)
	{
		string n = (name ?? "").Replace("(Instance)", "").Trim();
		if (string.IsNullOrEmpty(n)) n = "Meshy_Material";
		return string.Join("_", n.Split(Path.GetInvalidFileNameChars()));
	}

	static void ConfigureUrpMaterial(Material mat, Texture2D baseColor, Texture2D normal, Texture2D metallicGloss, Texture2D occlusion, Texture2D emission)
	{
		if (mat.HasProperty("_BaseColor")) mat.SetColor("_BaseColor", Color.white);
		if (baseColor != null && mat.HasProperty("_BaseMap")) mat.SetTexture("_BaseMap", baseColor);

		if (normal != null)
		{
			mat.SetTexture("_BumpMap", normal);
			mat.SetFloat("_BumpScale", 1f);
			mat.EnableKeyword("_NORMALMAP");
		}
		else
		{
			mat.SetTexture("_BumpMap", null);
			mat.DisableKeyword("_NORMALMAP");
		}

		if (metallicGloss != null)
		{
			mat.SetTexture("_MetallicGlossMap", metallicGloss);
			mat.SetFloat("_Metallic", 1f);
			mat.SetFloat("_Smoothness", 1f);
			if (mat.HasProperty("_SmoothnessTextureChannel")) mat.SetFloat("_SmoothnessTextureChannel", 0f); // Metallic Alpha
			mat.EnableKeyword("_METALLICSPECGLOSSMAP");
		}
		else
		{
			mat.SetTexture("_MetallicGlossMap", null);
			mat.DisableKeyword("_METALLICSPECGLOSSMAP");
		}

		if (occlusion != null)
		{
			mat.SetTexture("_OcclusionMap", occlusion);
			if (mat.HasProperty("_OcclusionStrength")) mat.SetFloat("_OcclusionStrength", 1f);
			mat.EnableKeyword("_OCCLUSIONMAP");
		}
		else
		{
			mat.SetTexture("_OcclusionMap", null);
			mat.DisableKeyword("_OCCLUSIONMAP");
		}

		ApplyStandardEmission(mat, emission);
	}

	static void ConfigureBuiltinMaterial(Material mat, Texture2D baseColor, Texture2D normal, Texture2D metallicGloss, Texture2D occlusion, Texture2D emission)
	{
		if (mat.HasProperty("_Color")) mat.SetColor("_Color", Color.white);
		if (baseColor != null && mat.HasProperty("_MainTex")) mat.SetTexture("_MainTex", baseColor);

		if (normal != null)
		{
			mat.SetTexture("_BumpMap", normal);
			mat.SetFloat("_BumpScale", 1f);
			mat.EnableKeyword("_NORMALMAP");
		}
		else
		{
			mat.SetTexture("_BumpMap", null);
			mat.DisableKeyword("_NORMALMAP");
		}

		if (metallicGloss != null)
		{
			mat.SetTexture("_MetallicGlossMap", metallicGloss);
			mat.SetFloat("_Metallic", 1f);
			if (mat.HasProperty("_Glossiness")) mat.SetFloat("_Glossiness", 1f);
			if (mat.HasProperty("_GlossMapScale")) mat.SetFloat("_GlossMapScale", 1f);
			if (mat.HasProperty("_SmoothnessTextureChannel")) mat.SetFloat("_SmoothnessTextureChannel", 0f);
			mat.EnableKeyword("_METALLICGLOSSMAP");
		}
		else
		{
			mat.SetTexture("_MetallicGlossMap", null);
			mat.DisableKeyword("_METALLICGLOSSMAP");
		}

		if (occlusion != null)
		{
			mat.SetTexture("_OcclusionMap", occlusion);
			if (mat.HasProperty("_OcclusionStrength")) mat.SetFloat("_OcclusionStrength", 1f);
		}
		else
		{
			mat.SetTexture("_OcclusionMap", null);
		}

		ApplyStandardEmission(mat, emission);
	}

	static void ConfigureHdrpMaterial(Material mat, Texture2D baseColor, Texture2D normal, Texture2D maskMap, Texture2D emission)
	{
		if (mat.HasProperty("_BaseColor")) mat.SetColor("_BaseColor", Color.white);
		if (baseColor != null && mat.HasProperty("_BaseColorMap")) mat.SetTexture("_BaseColorMap", baseColor);

		if (normal != null)
		{
			mat.SetTexture("_NormalMap", normal);
			mat.EnableKeyword("_NORMALMAP");
		}
		else
		{
			mat.SetTexture("_NormalMap", null);
			mat.DisableKeyword("_NORMALMAP");
		}

		if (maskMap != null)
		{
			mat.SetTexture("_MaskMap", maskMap);
			if (mat.HasProperty("_Metallic")) mat.SetFloat("_Metallic", 1f);
			if (mat.HasProperty("_Smoothness")) mat.SetFloat("_Smoothness", 1f);
			mat.EnableKeyword("_MASKMAP");
		}
		else
		{
			mat.SetTexture("_MaskMap", null);
			mat.DisableKeyword("_MASKMAP");
		}

		if (emission != null)
		{
			if (mat.HasProperty("_EmissiveColorMap")) mat.SetTexture("_EmissiveColorMap", emission);
			if (mat.HasProperty("_EmissiveColor")) mat.SetColor("_EmissiveColor", Color.white);
			if (mat.HasProperty("_UseEmissiveIntensity")) mat.SetFloat("_UseEmissiveIntensity", 0f);
			mat.EnableKeyword("_EMISSIVE_COLOR_MAP");
			mat.globalIlluminationFlags = MaterialGlobalIlluminationFlags.RealtimeEmissive;
		}
		else
		{
			if (mat.HasProperty("_EmissiveColorMap")) mat.SetTexture("_EmissiveColorMap", null);
			mat.DisableKeyword("_EMISSIVE_COLOR_MAP");
			mat.globalIlluminationFlags = MaterialGlobalIlluminationFlags.EmissiveIsBlack;
		}
	}

	static void ApplyStandardEmission(Material mat, Texture2D emission)
	{
		if (emission != null)
		{
			if (mat.HasProperty("_EmissionMap")) mat.SetTexture("_EmissionMap", emission);
			if (mat.HasProperty("_EmissionColor")) mat.SetColor("_EmissionColor", Color.white);
			mat.EnableKeyword("_EMISSION");
			mat.globalIlluminationFlags = MaterialGlobalIlluminationFlags.RealtimeEmissive;
		}
		else
		{
			if (mat.HasProperty("_EmissionMap")) mat.SetTexture("_EmissionMap", null);
			if (mat.HasProperty("_EmissionColor")) mat.SetColor("_EmissionColor", Color.black);
			mat.DisableKeyword("_EMISSION");
			mat.globalIlluminationFlags = MaterialGlobalIlluminationFlags.EmissiveIsBlack;
		}
	}

	static Texture2D ImportRawAsAsset(string srcFile, string modelDir, string assetBaseName, bool isNormal, bool sRGB)
	{
		if (string.IsNullOrEmpty(srcFile) || !File.Exists(srcFile)) return null;
		string ext = Path.GetExtension(srcFile);
		if (string.IsNullOrEmpty(ext)) ext = ".png";
		string assetPath = Path.Combine(modelDir, assetBaseName + ext).Replace('\\', '/');
		File.Copy(srcFile, assetPath, true);
		AssetDatabase.ImportAsset(assetPath, ImportAssetOptions.ForceUpdate);
		if (AssetImporter.GetAtPath(assetPath) is TextureImporter ti)
		{
			bool changed = false;
			TextureImporterType desired = isNormal ? TextureImporterType.NormalMap : TextureImporterType.Default;
			if (ti.textureType != desired) { ti.textureType = desired; changed = true; }
			if (!isNormal && ti.sRGBTexture != sRGB) { ti.sRGBTexture = sRGB; changed = true; }
			if (changed) ti.SaveAndReimport();
		}
		return AssetDatabase.LoadAssetAtPath<Texture2D>(assetPath);
	}

	static Texture2D ComposeMetallicSmoothnessMap(string metallicFile, string roughnessFile, string modelDir, string assetBaseName)
	{
		Texture2D metal = LoadReadableTexture(metallicFile);
		Texture2D rough = LoadReadableTexture(roughnessFile);
		if (metal == null && rough == null) return null;

		int w = metal != null ? metal.width : rough.width;
		int h = metal != null ? metal.height : rough.height;
		Color[] mp = SamplePixels(metal, w, h);
		Color[] rp = SamplePixels(rough, w, h);

		var outp = new Color[w * h];
		for (int i = 0; i < outp.Length; i++)
		{
			float m = mp != null ? mp[i].r : 0f;        // no metallic map -> dielectric (0)
			float r = rp != null ? rp[i].r : 0.5f;       // no roughness map -> mid
			outp[i] = new Color(m, m, m, 1f - r);        // RGB = metallic, A = smoothness
		}
		Texture2D result = SavePackedTexture(outp, w, h, modelDir, assetBaseName);
		if (metal != null) UnityEngine.Object.DestroyImmediate(metal);
		if (rough != null) UnityEngine.Object.DestroyImmediate(rough);
		return result;
	}

	static Texture2D ComposeHDRPMaskMap(string metallicFile, string roughnessFile, string aoFile, string modelDir, string assetBaseName)
	{
		Texture2D metal = LoadReadableTexture(metallicFile);
		Texture2D rough = LoadReadableTexture(roughnessFile);
		Texture2D ao = LoadReadableTexture(aoFile);
		if (metal == null && rough == null && ao == null) return null;

		int w = metal?.width ?? rough?.width ?? ao.width;
		int h = metal?.height ?? rough?.height ?? ao.height;
		Color[] mp = SamplePixels(metal, w, h);
		Color[] rp = SamplePixels(rough, w, h);
		Color[] ap = SamplePixels(ao, w, h);

		var outp = new Color[w * h];
		for (int i = 0; i < outp.Length; i++)
		{
			float m = mp != null ? mp[i].r : 0f;
			float occ = ap != null ? ap[i].r : 1f;
			float r = rp != null ? rp[i].r : 0.5f;
			outp[i] = new Color(m, occ, 0f, 1f - r);     // R = metallic, G = AO, B = detail, A = smoothness
		}
		Texture2D result = SavePackedTexture(outp, w, h, modelDir, assetBaseName);
		if (metal != null) UnityEngine.Object.DestroyImmediate(metal);
		if (rough != null) UnityEngine.Object.DestroyImmediate(rough);
		if (ao != null) UnityEngine.Object.DestroyImmediate(ao);
		return result;
	}

	static Texture2D SavePackedTexture(Color[] pixels, int w, int h, string modelDir, string assetBaseName)
	{
		Texture2D tex = new(w, h, TextureFormat.RGBA32, false, true);
		tex.SetPixels(pixels);
		tex.Apply();
		string assetPath = Path.Combine(modelDir, assetBaseName + ".png").Replace('\\', '/');
		File.WriteAllBytes(assetPath, tex.EncodeToPNG());
		UnityEngine.Object.DestroyImmediate(tex);
		AssetDatabase.ImportAsset(assetPath, ImportAssetOptions.ForceUpdate);
		if (AssetImporter.GetAtPath(assetPath) is TextureImporter ti && ti.sRGBTexture)
		{
			ti.sRGBTexture = false;
			ti.SaveAndReimport();
		}
		return AssetDatabase.LoadAssetAtPath<Texture2D>(assetPath);
	}

	static Texture2D LoadReadableTexture(string file)
	{
		if (string.IsNullOrEmpty(file) || !File.Exists(file)) return null;
		// linear: true — read stored bytes as raw data (no sRGB conversion) for data maps.
		Texture2D tex = new(2, 2, TextureFormat.RGBA32, false, true);
		if (!tex.LoadImage(File.ReadAllBytes(file)))
		{
			UnityEngine.Object.DestroyImmediate(tex);
			return null;
		}
		return tex;
	}

	static Color[] SamplePixels(Texture2D tex, int w, int h)
	{
		if (tex == null) return null;
		if (tex.width == w && tex.height == h) return tex.GetPixels();
		var outp = new Color[w * h];
		for (int y = 0; y < h; y++)
			for (int x = 0; x < w; x++)
				outp[y * w + x] = tex.GetPixelBilinear((x + 0.5f) / w, (y + 0.5f) / h);
		return outp;
	}

	static bool CheckAndAssignTexture(Material material, string propertyName, string modelDir, params string[] nameKeywords)
	{
		if (!material.HasProperty(propertyName) || material.GetTexture(propertyName) != null) return false;

		foreach (string keyword in nameKeywords)
		{
			Texture2D texture = FindTextureInDirectory(modelDir, keyword);
			if (texture == null)
				continue;

			// If assigning to a normal map property, ensure the texture is imported as NormalMap
			bool isNormalMapProperty = propertyName == "_BumpMap" || propertyName == "_NormalMap";
			if (isNormalMapProperty)
			{
				string texturePath = AssetDatabase.GetAssetPath(texture);
				if (AssetImporter.GetAtPath(texturePath) is TextureImporter textureImporter)
				{
					if (textureImporter.textureType != TextureImporterType.NormalMap)
					{
						textureImporter.textureType = TextureImporterType.NormalMap;
						textureImporter.SaveAndReimport();
						Debug.Log($"[Meshy Bridge] Set texture type to Normal Map for: {texture.name}");
						// Reload texture after reimport
						texture = AssetDatabase.LoadAssetAtPath<Texture2D>(texturePath);
					}
				}
			}

			material.SetTexture(propertyName, texture);
			Debug.Log($"[Meshy Bridge] Set {propertyName} texture for material {material.name}: {texture.name}");
			return true;
		}
		return false;
	}

	/// <summary>
	/// Ensures that a texture assigned to a normal map property has the correct import type.
	/// This fixes issues where embedded textures are auto-assigned but not marked as normal maps.
	/// </summary>
	static void EnsureNormalMapTextureType(Material material, string propertyName)
	{
		if (!material.HasProperty(propertyName)) return;
		
		Texture texture = material.GetTexture(propertyName);
		if (texture == null) return;

		string texturePath = AssetDatabase.GetAssetPath(texture);
		if (string.IsNullOrEmpty(texturePath)) return;

		if (AssetImporter.GetAtPath(texturePath) is TextureImporter textureImporter)
		{
			if (textureImporter.textureType != TextureImporterType.NormalMap)
			{
				textureImporter.textureType = TextureImporterType.NormalMap;
				textureImporter.SaveAndReimport();
				Debug.Log($"[Meshy Bridge] Fixed texture type to Normal Map for: {texture.name}");
			}
		}
	}

	static Texture2D FindTextureInDirectory(string directory, string nameKeyword)
	{
		string[] textureExtensions = { "*.jpg", "*.jpeg", "*.png", "*.tga", "*.bmp", "*.tiff", "*.tif" };

		foreach (string pattern in textureExtensions)
		{
			string[] textureFiles = Directory.GetFiles(directory, pattern, SearchOption.AllDirectories);
			foreach (string textureFile in textureFiles)
			{
				string fileName = Path.GetFileNameWithoutExtension(textureFile);
				if (fileName.ToLower().Contains(nameKeyword.ToLower()))
				{
					string relativePath = textureFile.Replace('\\', '/');
					return AssetDatabase.LoadAssetAtPath<Texture2D>(relativePath);
				}
			}
		}

		return null;
	}

	static Texture2D FindFirstTextureInDirectory(string directory)
	{
		string[] textureExtensions = { "*.jpg", "*.jpeg", "*.png", "*.tga", "*.bmp", "*.tiff", "*.tif" };

		foreach (string pattern in textureExtensions)
		{
			string[] textureFiles = Directory.GetFiles(directory, pattern, SearchOption.TopDirectoryOnly);
			if (textureFiles.Length > 0)
			{
				string relativePath = textureFiles[0].Replace('\\', '/');
				return AssetDatabase.LoadAssetAtPath<Texture2D>(relativePath);
			}
		}

		return null;
	}

	static void CleanupTempFile(string path)
	{
		try
		{
			if (File.Exists(path))
				File.Delete(path);
		}
		catch (Exception e)
		{
			Debug.LogError($"Error cleaning up: {e.Message}");
		}
	}
	
	static void CreateAnimatorControllerForMultipleClips(GameObject sceneObject, string modelPath)
	{
		AnimationClip[] clips = AssetDatabase.LoadAllAssetsAtPath(modelPath).OfType<AnimationClip>().ToArray();
		if (clips.Length <= 1) return;

		string controllerPath = modelPath.Replace(Path.GetExtension(modelPath), "_Controller.controller");

		AnimatorController controller = AnimatorController.CreateAnimatorControllerAtPath(controllerPath);

		for (int i = 0; i < clips.Length; i++)
		{
			AnimationClip clip = clips[i];
			AnimatorState state = controller.layers[0].stateMachine.AddState(clip.name);
			state.motion = clip;

			if (i == 0)
				controller.layers[0].stateMachine.defaultState = state;
		}

		if (sceneObject.GetComponent<Animator>() is { } animator)
			animator.runtimeAnimatorController = controller;

		Debug.Log($"[Meshy Bridge] Created AnimatorController with {clips.Length} animation clips");
	}
	
	public static RenderPipeline GetActiveRenderPipeline()
	{
		if (GraphicsSettings.currentRenderPipeline == null)
			return RenderPipeline.BuiltIn;

		string pipelineAssetName = GraphicsSettings.currentRenderPipeline.GetType().Name;

		if (pipelineAssetName.Contains("UniversalRenderPipelineAsset"))
			return RenderPipeline.URP;
            
		if (pipelineAssetName.Contains("HDRenderPipelineAsset"))
			return RenderPipeline.HDRP;

		return RenderPipeline.Unsupported;
	}
}