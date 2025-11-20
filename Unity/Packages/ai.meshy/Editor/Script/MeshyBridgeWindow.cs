using UnityEngine;
using UnityEditor;
using System.Net;
using System;
using System.Net.Sockets;
using System.Threading;
using System.Text;
using System.IO;
using System.Collections.Generic;
using System.IO.Compression;
using UnityEditor.SceneManagement;
using System.Linq;
using UnityEditor.Animations;

public class MeshyBridgeWindow : EditorWindow
{
	private static string _tempCachePath;

	private static Thread serverThread;
	private static Thread guardThread;
	private static bool _serverStop;
	private static TcpListener listener;

	private static readonly Queue<MeshTransfer> importQueue = new();

	public static bool IsRunning { get; private set; }

	private GUIContent runButtonContent;
	private GUIContent stopButtonContent;

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
		window.minSize = new(250, 100);
		window.maxSize = new(400, 150);
	}

	private void OnEnable()
	{
		runButtonContent = new("Run Bridge");
		stopButtonContent = new("Bridge ON");

		_tempCachePath = Application.temporaryCachePath;
		EditorApplication.update += Update;
		StartServer();
	}

	private void OnDisable()
	{
		EditorApplication.update -= Update;
		StopServer(true);
	}

	private void OnGUI()
	{
		EditorGUILayout.BeginVertical();
		GUILayout.Space(10);
		GUIStyle buttonStyle = new(GUI.skin.button) { fontSize = 14, fontStyle = FontStyle.Bold, fixedHeight = 40 };
		Color originalColor = GUI.backgroundColor;
		if (IsRunning) GUI.backgroundColor = new Color(0.4f, 0.6f, 1.0f);
		GUIContent currentContent = IsRunning ? stopButtonContent : runButtonContent;
		if (GUILayout.Button(currentContent, buttonStyle)) ToggleBridgeState();
		GUI.backgroundColor = originalColor;
		EditorGUILayout.EndVertical();
		Repaint();
	}

	private void ToggleBridgeState()
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

	private static void GuardJob()
	{
		while (!_serverStop)
			Thread.Sleep(200);

		listener?.Stop();
		Debug.Log("[Meshy Bridge] Guard thread shutting down server");
	}

	private static void RunServer()
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
					using TcpClient client = listener.AcceptTcpClient();
					using NetworkStream stream = client.GetStream();
					ProcessClientRequest(stream);
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

	private static readonly string[] allowedOrigins =
	{
		"https://www.meshy.ai",
		"http://localhost:3700"
	};


	private static void ProcessClientRequest(NetworkStream stream)
	{
		try
		{
			Debug.Log("[Meshy Bridge] Processing request");
			byte[] buffer = new byte[1024 * 16];
			int bytesRead = stream.Read(buffer, 0, buffer.Length);
			string request = Encoding.UTF8.GetString(buffer, 0, bytesRead);
			Debug.Log($"[Meshy Bridge] Received request ({bytesRead} bytes):\n{request}");

			string[] requestLines = request.Split(new[] { "\r\n" }, StringSplitOptions.None);

			if (requestLines.Length == 0)
			{
				Debug.LogWarning("[Meshy Bridge] Empty request");
				SendErrorResponse(stream, "Empty request");
				return;
			}

			string[] requestParts = requestLines[0].Split(' ');
			if (requestParts.Length < 2)
			{
				Debug.LogWarning("[Meshy Bridge] Invalid request format: " + requestLines[0]);
				SendErrorResponse(stream, "Invalid request format");
				return;
			}

			string method = requestParts[0];
			string path = requestParts[1];
			string origin = GetHeaderValue(requestLines, "Origin");

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
				ProcessImportRequest(stream, request, origin);
				return;
			}

			SendNotFoundResponse(stream, origin);
		}
		catch (Exception e)
		{
			Debug.LogError($"[Meshy Bridge] Error processing request: {e.GetType().Name} - {e.Message}\n{e.StackTrace}");
			try
			{
				SendErrorResponse(stream, e.Message);
			}
			catch (Exception ex)
			{
				Debug.LogError($"[Meshy Bridge] Failed to send error response: {ex.Message}");
			}
		}
	}

	[Serializable]
	private class ImportResponseData
	{
		public string status;
		public string message;
		public string path;
	}

	private static void ProcessImportRequest(NetworkStream stream, string request, string origin)
	{
		try
		{
			int jsonStart = request.IndexOf('{');
			if (jsonStart < 0)
				throw new Exception("Invalid request format: JSON not found");

			string jsonBody = request.Substring(jsonStart);
			if (!jsonBody.Trim().StartsWith("{") || !jsonBody.Trim().EndsWith("}"))
				throw new Exception("Invalid JSON format");

			ImportRequestData data = JsonUtility.FromJson<ImportRequestData>(jsonBody);

			string fileName = $"bridge_model_{DateTime.Now:yyyyMMdd_HHmmss}_{Guid.NewGuid().ToString("N")[..8]}";
			string filePath = Path.Combine(Path.GetTempPath(), "Meshy", fileName);

			Directory.CreateDirectory(Path.GetDirectoryName(filePath));

			if (File.Exists(filePath))
			{
				File.Delete(filePath);
				Debug.Log($"[Meshy Bridge] Deleted existing file: {filePath}");
			}

			Debug.Log($"[Meshy Bridge] Downloading: {data.url}");
			using (WebClient client = new())
			{
				client.DownloadFile(data.url, filePath);
			}

			string fileExtension = ".glb";
			byte[] header = new byte[4];
			using (FileStream fs = new(filePath, FileMode.Open, FileAccess.Read))
			{
				fs.Read(header, 0, header.Length);

				if (data.format.ToLower() == "glb")
				{
					if (header[0] == 'P' && header[1] == 'K' && header[2] == 0x03 && header[3] == 0x04)
						fileExtension = ".zip";
					else if (header[0] == 'g' && header[1] == 'l' && header[2] == 'T' && header[3] == 'F')
						fileExtension = ".glb";
				}
				else if (data.format.ToLower() == "fbx")
				{
					if (header[0] == 'P' && header[1] == 'K' && header[2] == 0x03 && header[3] == 0x04)
						fileExtension = ".zip";
					else
						fileExtension = ".fbx";
				}
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
						file_format = data.format,
						path = filePath,
						name = data.name ?? "",
						frameRate = data.frameRate
					});
			}

			ImportResponseData responseData = new()
			{
				status = "ok",
				message = "File queued for import",
				path = filePath
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
			Debug.Log("[Meshy Bridge] Response sent");
		}
		catch (Exception e)
		{
			Debug.LogError($"[Meshy Bridge] Error processing import request: {e.Message}");
			SendErrorResponse(stream, e.Message);
		}
	}

	[Serializable]
	private class ImportRequestData
	{
		public string url;
		public string format;
		public string name;
		public int frameRate = 30;
	}

	private static string GetAllowedOrigin(string origin) =>
		Array.Exists(allowedOrigins, o => string.Equals(o, origin, StringComparison.OrdinalIgnoreCase)) ? origin : "https://www.meshy.ai";

	private static void SendOptionsResponse(NetworkStream stream, string origin)
	{
		string response = $"HTTP/1.1 200 OK\r\n" +
		                  $"Access-Control-Allow-Origin: {GetAllowedOrigin(origin)}\r\n" +
		                  "Access-Control-Allow-Methods: POST, GET, OPTIONS\r\n" +
		                  "Access-Control-Allow-Headers: *\r\n" +
		                  "Access-Control-Max-Age: 86400\r\n\r\n";
		stream.Write(Encoding.UTF8.GetBytes(response), 0, response.Length);
	}

	[Serializable]
	private class StatusResponseData
	{
		public string status = "ok";
		public string dcc = "unity";
		public string version;
	}

	private static void SendStatusResponse(NetworkStream stream, string origin)
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

	private static void SendNotFoundResponse(NetworkStream stream, string origin)
	{
		string response = $"HTTP/1.1 404 Not Found\r\n" +
		                  $"Access-Control-Allow-Origin: {GetAllowedOrigin(origin)}\r\n" +
		                  "Content-Type: application/json\r\n\r\n" +
		                  JsonUtility.ToJson(new { status = "path not found" });
		stream.Write(Encoding.UTF8.GetBytes(response), 0, response.Length);
	}

	private static void SendErrorResponse(NetworkStream stream, string message)
	{
		try
		{
			string jsonBody = JsonUtility.ToJson(new { status = "error", message });
			string response = string.Join(
				"\r\n",
				"HTTP/1.1 500 Internal Server Error",
				"Access-Control-Allow-Origin: *",
				"Content-Type: application/json; charset=utf-8",
				"Connection: close",
				$"Content-Length: {jsonBody.Length}",
				"",
				jsonBody);

			byte[] responseBytes = Encoding.UTF8.GetBytes(response);
			stream.Write(responseBytes, 0, responseBytes.Length);
			stream.Flush();
		}
		catch (Exception e)
		{
			Debug.LogError($"[Meshy Bridge] Failed to send error response: {e.Message}");
		}
	}

	private static string GetHeaderValue(string[] headers, string headerName)
	{
		foreach (string header in headers)
			if (header.StartsWith(headerName + ":"))
				return header.Substring(headerName.Length + 1).Trim();
		return "";
	}

	private static void Update()
	{
		lock (importQueue)
		{
			while (importQueue.Count > 0)
			{
				MeshTransfer transfer = importQueue.Dequeue();
				ProcessMeshTransfer(transfer);
			}
		}
	}

	private static void ProcessMeshTransfer(MeshTransfer transfer)
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

	private static void ImportModelWithMaterial(MeshTransfer transfer)
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
				sceneObject.transform.rotation = Quaternion.identity;
				sceneObject.transform.localScale = Vector3.one;

				Animator animator = sceneObject.GetComponent<Animator>() ?? sceneObject.AddComponent<Animator>();

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

	private static void AddDefaultMaterial(GameObject obj)
	{
		Renderer renderer = obj.GetComponent<Renderer>();
		if (renderer != null && renderer.sharedMaterials.Length == 0)
		{
			Material material = new(Shader.Find("Standard")) { name = "Meshy_Material" };
			renderer.sharedMaterial = material;
		}
	}

	private static void ProcessZipFile(MeshTransfer transfer)
	{
		string extractPath = Path.Combine(_tempCachePath, "extracted");
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

	private static void ImportFBXWithTextures(MeshTransfer transfer)
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

			string sourceDir = Path.GetDirectoryName(transfer.path);
			ImportTextureFiles(sourceDir, modelDir);

			AssetDatabase.Refresh();

			AssetDatabase.ImportAsset(fbxRelativePath, ImportAssetOptions.ForceUpdate);

			GameObject importedObject = AssetDatabase.LoadAssetAtPath<GameObject>(fbxRelativePath);
			if (importedObject == null) return;

			importedObject.name = modelName;

			FixMaterialTextureReferences(importedObject, modelDir);

			EditorUtility.SetDirty(importedObject);
			AssetDatabase.SaveAssets();

			EditorApplication.delayCall += () =>
			{
				if (PrefabUtility.InstantiatePrefab(importedObject) is not GameObject sceneObject) return;

				sceneObject.transform.position = Vector3.zero;
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

	private static void ImportTextureFiles(string sourceDir, string targetDir)
	{
		string[] textureExtensions = { "*.jpg", "*.jpeg", "*.png", "*.tga", "*.bmp", "*.tiff", "*.tif", "*.exr", "*.hdr" };

		foreach (string pattern in textureExtensions)
		{
			string[] textureFiles = Directory.GetFiles(sourceDir, pattern, SearchOption.TopDirectoryOnly);
			foreach (string textureFile in textureFiles)
			{
				string fileName = Path.GetFileName(textureFile);
				string targetPath = Path.Combine(targetDir, fileName);

				File.Copy(textureFile, targetPath, true);
				Debug.Log($"[Meshy Bridge] Copied texture file: {fileName}");
			}
		}

		string[] subDirectories = Directory.GetDirectories(sourceDir);
		foreach (string subDir in subDirectories)
		{
			string subDirName = Path.GetFileName(subDir);
			string targetSubDir = Path.Combine(targetDir, subDirName);

			Directory.CreateDirectory(targetSubDir);

			foreach (string pattern in textureExtensions)
			{
				string[] textureFiles = Directory.GetFiles(subDir, pattern, SearchOption.AllDirectories);
				foreach (string textureFile in textureFiles)
				{
					string relativePath = Path.GetRelativePath(subDir, textureFile);
					string targetPath = Path.Combine(targetSubDir, relativePath);

					Directory.CreateDirectory(Path.GetDirectoryName(targetPath));

					File.Copy(textureFile, targetPath, true);
					Debug.Log($"[Meshy Bridge] Copied subdirectory texture file: {subDirName}/{relativePath}");
				}
			}
		}
	}

	private static void FixMaterialTextureReferences(GameObject fbxObject, string modelDir)
	{
		Renderer[] renderers = fbxObject.GetComponentsInChildren<Renderer>();

		foreach (Renderer renderer in renderers)
		{
			Material[] materials = renderer.sharedMaterials;

			for (int i = 0; i < materials.Length; i++)
			{
				Material material = materials[i];
				if (material == null) continue;

				if (material.mainTexture == null)
				{
					string textureName = material.name;
					Texture2D foundTexture = FindTextureInDirectory(modelDir, textureName);

					if (foundTexture != null)
					{
						material.mainTexture = foundTexture;
						Debug.Log($"[Meshy Bridge] Set texture for material {material.name}: {foundTexture.name}");
					}
					else
					{
						Texture2D firstTexture = FindFirstTextureInDirectory(modelDir);
						if (firstTexture != null)
						{
							material.mainTexture = firstTexture;
							Debug.Log($"[Meshy Bridge] Set default texture for material {material.name}: {firstTexture.name}");
						}
					}
				}

				CheckAndAssignTexture(material, "_BumpMap", modelDir, "normal", "Normal");
				CheckAndAssignTexture(material, "_MetallicGlossMap", modelDir, "metallic", "Metallic");
				CheckAndAssignTexture(material, "_OcclusionMap", modelDir, "occlusion", "AO");
				CheckAndAssignTexture(material, "_EmissionMap", modelDir, "emission", "Emissive");
			}
		}
	}

	private static void CheckAndAssignTexture(Material material, string propertyName, string modelDir, params string[] nameKeywords)
	{
		if (!material.HasProperty(propertyName) || material.GetTexture(propertyName) != null) return;

		foreach (string keyword in nameKeywords)
		{
			Texture2D texture = FindTextureInDirectory(modelDir, keyword);
			if (texture == null) continue;

			material.SetTexture(propertyName, texture);
			Debug.Log($"[Meshy Bridge] Set {propertyName} texture for material {material.name}: {texture.name}");
			break;
		}
	}

	private static Texture2D FindTextureInDirectory(string directory, string nameKeyword)
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

	private static Texture2D FindFirstTextureInDirectory(string directory)
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

	private static void CleanupTempFile(string path)
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
	
	private static void CreateAnimatorControllerForMultipleClips(GameObject sceneObject, string modelPath)
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
}