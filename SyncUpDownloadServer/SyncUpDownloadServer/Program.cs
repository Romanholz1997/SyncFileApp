using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

class TcpServerApp
{
    private static TcpListener _listener;
    private static readonly List<ClientInfo> _connectedClients = new List<ClientInfo>();
    private static readonly object _clientsLock = new object();

    class ClientInfo
    {
        public TcpClient Client { get; set; }
        public NetworkStream Stream { get; set; }
        public string FolderPathToWatch { get; set; }
        public Dictionary<string, bool> PathIsDirectory { get; set; } = new Dictionary<string, bool>();
        public object PathIsDirectoryLock { get; set; } = new object();
        public FileSystemWatcher Watcher { get; set; }
    }

    static void Main()
    {
        // Start listening for incoming connections
        _listener = new TcpListener(IPAddress.Any, 5000);
        _listener.Start();
        Console.WriteLine("Server started. Waiting for connections...");

        while (true)
        {
            // Accept a new client
            TcpClient client = _listener.AcceptTcpClient();
            IPEndPoint clientEndPoint = client.Client.RemoteEndPoint as IPEndPoint;
            if (clientEndPoint != null)
            {
                string clientIp = clientEndPoint.Address.ToString().Replace(":", "_"); // Replace ':' to avoid path issues
                int clientPort = clientEndPoint.Port;
                string clientFolder = Path.Combine(@"C:\Server", $"{clientIp}");

                if (!Directory.Exists(clientFolder))
                {
                    Directory.CreateDirectory(clientFolder);
                }

                // Initialize ClientInfo
                ClientInfo clientInfo = new ClientInfo
                {
                    Client = client,
                    Stream = client.GetStream(),
                    FolderPathToWatch = clientFolder
                };

                // Initialize Path Cache
                InitializePathCache(clientInfo);

                // Start FileSystemWatcher for this client
                StartFileWatcher(clientInfo);

                Console.WriteLine($"Client connected from IP: {clientEndPoint.Address}, Port: {clientPort}");

                // Add client to the list
                lock (_clientsLock)
                {
                    _connectedClients.Add(clientInfo);
                }

                // Handle the client in a new thread
                Thread clientThread = new Thread(() => HandleClient(clientInfo));
                clientThread.Start();
            }
            else
            {
                Console.WriteLine("Client connected. Could not retrieve IP address.");
            }
        }
    }

    private static void InitializePathCache(ClientInfo clientInfo)
    {
        lock (clientInfo.PathIsDirectoryLock)
        {
            clientInfo.PathIsDirectory.Clear();
            foreach (string dir in Directory.GetDirectories(clientInfo.FolderPathToWatch, "*", SearchOption.AllDirectories))
            {
                clientInfo.PathIsDirectory[dir] = true;
            }
            foreach (string file in Directory.GetFiles(clientInfo.FolderPathToWatch, "*", SearchOption.AllDirectories))
            {
                clientInfo.PathIsDirectory[file] = false;
            }
        }
    }

    private static void StartFileWatcher(ClientInfo clientInfo)
    {
        FileSystemWatcher watcher = new FileSystemWatcher
        {
            Path = clientInfo.FolderPathToWatch,
            IncludeSubdirectories = true, // Monitor subdirectories
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size
        };

        // Subscribe to events
        watcher.Changed += (s, e) => OnServerFileChanged(s, e, clientInfo);
        watcher.Created += (s, e) => OnServerFileCreated(s, e, clientInfo);
        watcher.Deleted += (s, e) => OnServerFileDeleted(s, e, clientInfo);
        watcher.Renamed += (s, e) => OnServerFileRenamed(s, e, clientInfo);

        // Start watching
        watcher.EnableRaisingEvents = true;
        clientInfo.Watcher = watcher;

        Console.WriteLine($"Server is watching for changes in {clientInfo.FolderPathToWatch}...");
    }

    private static void OnServerFileCreated(object sender, FileSystemEventArgs e, ClientInfo clientInfo)
    {
        bool isDirectory = Directory.Exists(e.FullPath);
        lock (clientInfo.PathIsDirectoryLock)
        {
            clientInfo.PathIsDirectory[e.FullPath] = isDirectory;
        }

        if (isDirectory)
        {
            Console.WriteLine($"[Server][{clientInfo.FolderPathToWatch}] Directory created: {e.FullPath}");
            NotifyClientAboutDirectoryChange("CREATE_DIRECTORY", e.FullPath, clientInfo);
        }
        else if (File.Exists(e.FullPath))
        {
            Console.WriteLine($"[Server][{clientInfo.FolderPathToWatch}] File created on server: {e.FullPath}");
            NotifyClientAboutFileChange("DOWNLOAD_FILE", e.FullPath, clientInfo);
        }
    }

    private static void OnServerFileChanged(object sender, FileSystemEventArgs e, ClientInfo clientInfo)
    {
        if (Directory.Exists(e.FullPath))
            return; // Changed events for directories are not handled

        lock (clientInfo.PathIsDirectoryLock)
        {
            clientInfo.PathIsDirectory[e.FullPath] = false;
        }

        if (File.Exists(e.FullPath))
        {
            Console.WriteLine($"[Server][{clientInfo.FolderPathToWatch}] File changed on server: {e.FullPath}");
            NotifyClientAboutFileChange("DOWNLOAD_FILE", e.FullPath, clientInfo);
        }
        else
        {
            Console.WriteLine($"[Server][{clientInfo.FolderPathToWatch}] Changed item is not a file or does not exist: {e.FullPath}");
        }
    }

    private static void OnServerFileDeleted(object sender, FileSystemEventArgs e, ClientInfo clientInfo)
    {
        bool isDirectory = false;
        lock (clientInfo.PathIsDirectoryLock)
        {
            if (clientInfo.PathIsDirectory.TryGetValue(e.FullPath, out isDirectory))
            {
                clientInfo.PathIsDirectory.Remove(e.FullPath);

                // Remove all subpaths if it's a directory
                if (isDirectory)
                {
                    var keysToRemove = new List<string>();
                    foreach (var key in clientInfo.PathIsDirectory.Keys)
                    {
                        if (key.StartsWith(e.FullPath + Path.DirectorySeparatorChar))
                        {
                            keysToRemove.Add(key);
                        }
                    }

                    foreach (var key in keysToRemove)
                    {
                        clientInfo.PathIsDirectory.Remove(key);
                    }
                }
            }
            else
            {
                // Cannot determine if the deleted item was a file or directory
                Console.WriteLine($"[Server][{clientInfo.FolderPathToWatch}] Deleted item not found in path cache: {e.FullPath}");
            }
        }

        if (isDirectory)
        {
            Console.WriteLine($"[Server][{clientInfo.FolderPathToWatch}] Directory deleted: {e.FullPath}");
            NotifyClientAboutDirectoryChange("DELETE_DIRECTORY", e.FullPath, clientInfo);
        }
        else
        {
            Console.WriteLine($"[Server][{clientInfo.FolderPathToWatch}] File deleted on server: {e.FullPath}");
            NotifyClientAboutFileChange("DELETE_FILE", e.FullPath, clientInfo);
        }
    }

    private static void OnServerFileRenamed(object sender, RenamedEventArgs e, ClientInfo clientInfo)
    {
        bool isDirectory = false;
        lock (clientInfo.PathIsDirectoryLock)
        {
            if (clientInfo.PathIsDirectory.TryGetValue(e.OldFullPath, out isDirectory))
            {
                clientInfo.PathIsDirectory.Remove(e.OldFullPath);
                clientInfo.PathIsDirectory[e.FullPath] = isDirectory;
            }
            else
            {
                // Item not found in the dictionary
                Console.WriteLine($"[Server][{clientInfo.FolderPathToWatch}] Renamed item was not found in dictionary: {e.OldFullPath}");
                return;
            }
        }

        if (isDirectory)
        {
            Console.WriteLine($"[Server][{clientInfo.FolderPathToWatch}] Directory renamed on server from {e.OldFullPath} to {e.FullPath}");
            NotifyClientAboutDirectoryRename(e.OldFullPath, e.FullPath, clientInfo);
        }
        else
        {
            Console.WriteLine($"[Server][{clientInfo.FolderPathToWatch}] File renamed on server from {e.OldFullPath} to {e.FullPath}");
            NotifyClientAboutFileRename(e.OldFullPath, e.FullPath, clientInfo);
        }
    }

    private static void NotifyClientAboutDirectoryRename(string oldDirectoryPath, string newDirectoryPath, ClientInfo clientInfo)
    {
        // Compute relative paths from the folder being watched
        string relativeOldPath = Path.GetRelativePath(clientInfo.FolderPathToWatch, oldDirectoryPath).Replace("\\", "/");
        string relativeNewPath = Path.GetRelativePath(clientInfo.FolderPathToWatch, newDirectoryPath).Replace("\\", "/");

        // Construct the message
        string message = $"RENAME_DIRECTORY \"{relativeOldPath}\" \"{relativeNewPath}\"\n";

        SendMessageToClient(message, clientInfo);
    }

    private static void NotifyClientAboutDirectoryChange(string command, string directoryPath, ClientInfo clientInfo)
    {
        string relativePath = Path.GetRelativePath(clientInfo.FolderPathToWatch, directoryPath).Replace("\\", "/");
        string message = $"{command} \"{relativePath}\"\n";

        SendMessageToClient(message, clientInfo);
    }

    private static void NotifyClientAboutFileChange(string command, string filePath, ClientInfo clientInfo)
    {
        string relativePath = Path.GetRelativePath(clientInfo.FolderPathToWatch, filePath).Replace("\\", "/");
        string message = $"{command} \"{relativePath}\"\n";

        lock (clientInfo.PathIsDirectoryLock)
        {
            SendMessageToClient(message, clientInfo);

            // If command is DOWNLOAD_FILE, send the file size and data
            if (command == "DOWNLOAD_FILE")
            {
                try
                {
                    // Send file size
                    FileInfo fileInfo = new FileInfo(filePath);
                    long fileSize = fileInfo.Length;
                    string sizeMessage = $"{fileSize}\n";
                    byte[] sizeBytes = Encoding.UTF8.GetBytes(sizeMessage);
                    clientInfo.Stream.Write(sizeBytes, 0, sizeBytes.Length);

                    // Send file data
                    using (FileStream fs = new FileStream(filePath, FileMode.Open, FileAccess.Read))
                    {
                        fs.CopyTo(clientInfo.Stream);
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[Server][{clientInfo.FolderPathToWatch}] Error sending file data: {ex.Message}");
                }
            }
        }
    }

    private static void NotifyClientAboutFileRename(string oldFilePath, string newFilePath, ClientInfo clientInfo)
    {
        string oldFileName = Path.GetRelativePath(clientInfo.FolderPathToWatch, oldFilePath).Replace("\\", "/");
        string newFileName = Path.GetRelativePath(clientInfo.FolderPathToWatch, newFilePath).Replace("\\", "/");
        string message = $"RENAME_FILE \"{oldFileName}\" \"{newFileName}\"\n";

        SendMessageToClient(message, clientInfo);
    }

    private static void SendMessageToClient(string message, ClientInfo clientInfo)
    {
        try
        {
            byte[] messageBytes = Encoding.UTF8.GetBytes(message);
            clientInfo.Stream.Write(messageBytes, 0, messageBytes.Length);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Server][{clientInfo.FolderPathToWatch}] Error sending message to client: {ex.Message}");
            // Optionally handle client disconnection
        }
    }

    private static void HandleClient(ClientInfo clientInfo)
    {
        try
        {
            NetworkStream stream = clientInfo.Stream;
            while (true)
            {
                // Read the command line from the client
                string commandLine = ReadLineFromStream(stream);
                if (string.IsNullOrEmpty(commandLine))
                {
                    // Client disconnected
                    break;
                }

                string command = commandLine.Trim();

                if (command.StartsWith("UPLOAD_FILE "))
                {
                    string commandArgs = command.Substring("UPLOAD_FILE ".Length).Trim();
                    HandleUploadFile(commandArgs, stream, clientInfo);
                }
                else if (command.StartsWith("RENAME_FILE "))
                {
                    string commandArgs = command.Substring("RENAME_FILE ".Length).Trim();
                    HandleRenameFile(commandArgs, clientInfo);
                }
                else if (command.StartsWith("DELETE_FILE "))
                {
                    string commandArgs = command.Substring("DELETE_FILE ".Length).Trim();
                    HandleDeleteFile(commandArgs, clientInfo);
                }
                else if (command.StartsWith("CREATE_DIRECTORY "))
                {
                    string commandArgs = command.Substring("CREATE_DIRECTORY ".Length).Trim();
                    HandleCreateDirectory(commandArgs, clientInfo);
                }
                else if (command.StartsWith("RENAME_DIRECTORY "))
                {
                    string commandArgs = command.Substring("RENAME_DIRECTORY ".Length).Trim();
                    HandleRenameDirectory(commandArgs, clientInfo);
                }
                else if (command.StartsWith("DELETE_DIRECTORY "))
                {
                    string commandArgs = command.Substring("DELETE_DIRECTORY ".Length).Trim();
                    HandleDeleteDirectory(commandArgs, clientInfo);
                }
                else
                {
                    Console.WriteLine($"[Server][{clientInfo.FolderPathToWatch}] Invalid command received: {command}");
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Server][{clientInfo.FolderPathToWatch}] Error handling client: {ex.Message}");
        }
        finally
        {
            // Remove client from the list
            lock (_clientsLock)
            {
                _connectedClients.Remove(clientInfo);
            }
            clientInfo.Client.Close();
            Console.WriteLine($"[Server][{clientInfo.FolderPathToWatch}] Client disconnected.");
        }
    }

    private static void HandleUploadFile(string commandArgs, NetworkStream stream, ClientInfo clientInfo)
    {
        // Regular expression to match a quoted string and file size
        Regex regex = new Regex("^\"([^\"]+)\"\\s+(\\d+)$");
        Match match = regex.Match(commandArgs);
        if (match.Success)
        {
            string fileName = match.Groups[1].Value;
            string fileSizeStr = match.Groups[2].Value;

            if (!long.TryParse(fileSizeStr, out long fileSize))
            {
                Console.WriteLine($"[Server][{clientInfo.FolderPathToWatch}] Invalid file size.");
                return;
            }

            Console.WriteLine($"[Server][{clientInfo.FolderPathToWatch}] Receiving file: {fileName}, size: {fileSize}");

            // Specify the path to save the uploaded file
            string savePath = Path.Combine(clientInfo.FolderPathToWatch, fileName);

            // Ensure the directory exists
            string directoryPath = Path.GetDirectoryName(savePath);
            if (!Directory.Exists(directoryPath))
            {
                Directory.CreateDirectory(directoryPath);
            }

            // Create a FileStream to save the uploaded file
            using (FileStream fs = new FileStream(savePath, FileMode.Create, FileAccess.Write))
            {
                byte[] buffer = new byte[4096];
                long totalBytesRead = 0;
                int readBytes;

                // Read the file data based on the file size
                while (totalBytesRead < fileSize && (readBytes = stream.Read(buffer, 0, buffer.Length)) > 0)
                {
                    fs.Write(buffer, 0, readBytes);
                    totalBytesRead += readBytes;
                }

                if (totalBytesRead != fileSize)
                {
                    Console.WriteLine($"[Server][{clientInfo.FolderPathToWatch}] Warning: Expected file size does not match the received data.");
                }
            }

            Console.WriteLine($"[Server][{clientInfo.FolderPathToWatch}] File received and saved as: {savePath}");
        }
        else
        {
            Console.WriteLine($"[Server][{clientInfo.FolderPathToWatch}] Invalid UPLOAD_FILE command format.");
        }
    }

    private static void HandleRenameFile(string commandArgs, ClientInfo clientInfo)
    {
        // Regular expression to match two quoted strings
        Regex regex = new Regex("^\"([^\"]+)\"\\s+\"([^\"]+)\"$");
        Match match = regex.Match(commandArgs);
        if (match.Success)
        {
            string oldFileName = match.Groups[1].Value;
            string newFileName = match.Groups[2].Value;

            string oldFilePath = Path.Combine(clientInfo.FolderPathToWatch, oldFileName);
            string newFilePath = Path.Combine(clientInfo.FolderPathToWatch, newFileName);

            try
            {
                if (File.Exists(oldFilePath))
                {
                    // Ensure the target directory exists
                    string newDirectoryPath = Path.GetDirectoryName(newFilePath);
                    if (!Directory.Exists(newDirectoryPath))
                    {
                        Directory.CreateDirectory(newDirectoryPath);
                    }

                    File.Move(oldFilePath, newFilePath);
                    Console.WriteLine($"[Server][{clientInfo.FolderPathToWatch}] File renamed from {oldFileName} to {newFileName}");
                }
                else
                {
                    Console.WriteLine($"[Server][{clientInfo.FolderPathToWatch}] File not found: {oldFileName}");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Server][{clientInfo.FolderPathToWatch}] Error renaming file: {ex.Message}");
            }
        }
        else
        {
            Console.WriteLine($"[Server][{clientInfo.FolderPathToWatch}] Invalid RENAME_FILE command format.");
        }
    }

    private static void HandleDeleteFile(string commandArgs, ClientInfo clientInfo)
    {
        // Regular expression to match a quoted string
        Regex regex = new Regex("^\"([^\"]+)\"$");
        Match match = regex.Match(commandArgs);
        if (match.Success)
        {
            string fileName = match.Groups[1].Value;
            string filePath = Path.Combine(clientInfo.FolderPathToWatch, fileName);

            try
            {
                if (File.Exists(filePath))
                {
                    File.Delete(filePath);
                    Console.WriteLine($"[Server][{clientInfo.FolderPathToWatch}] File deleted: {fileName}");
                }
                else
                {
                    Console.WriteLine($"[Server][{clientInfo.FolderPathToWatch}] File not found: {fileName}");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Server][{clientInfo.FolderPathToWatch}] Error deleting file: {ex.Message}");
            }
        }
        else
        {
            Console.WriteLine($"[Server][{clientInfo.FolderPathToWatch}] Invalid DELETE_FILE command format.");
        }
    }

    private static void HandleCreateDirectory(string commandArgs, ClientInfo clientInfo)
    {
        // Regular expression to match a quoted directory path
        Regex regex = new Regex("^\"([^\"]+)\"$");
        Match match = regex.Match(commandArgs);
        if (match.Success)
        {
            string relativePath = match.Groups[1].Value;
            string directoryPath = Path.Combine(clientInfo.FolderPathToWatch, relativePath);

            try
            {
                if (!Directory.Exists(directoryPath))
                {
                    Directory.CreateDirectory(directoryPath);
                    Console.WriteLine($"[Server][{clientInfo.FolderPathToWatch}] Directory created: {directoryPath}");
                }
                else
                {
                    Console.WriteLine($"[Server][{clientInfo.FolderPathToWatch}] Directory already exists: {directoryPath}");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Server][{clientInfo.FolderPathToWatch}] Error creating directory: {ex.Message}");
            }
        }
        else
        {
            Console.WriteLine($"[Server][{clientInfo.FolderPathToWatch}] Invalid CREATE_DIRECTORY command format.");
        }
    }

    private static void HandleRenameDirectory(string commandArgs, ClientInfo clientInfo)
    {
        // Regular expression to match two quoted directory paths
        Regex regex = new Regex("^\"([^\"]+)\"\\s+\"([^\"]+)\"$");
        Match match = regex.Match(commandArgs);
        if (match.Success)
        {
            string oldRelativePath = match.Groups[1].Value;
            string newRelativePath = match.Groups[2].Value;
            string oldDirectoryPath = Path.Combine(clientInfo.FolderPathToWatch, oldRelativePath);
            string newDirectoryPath = Path.Combine(clientInfo.FolderPathToWatch, newRelativePath);

            try
            {
                if (Directory.Exists(oldDirectoryPath))
                {
                    Directory.Move(oldDirectoryPath, newDirectoryPath);
                    Console.WriteLine($"[Server][{clientInfo.FolderPathToWatch}] Directory renamed from {oldRelativePath} to {newRelativePath}");
                }
                else
                {
                    Console.WriteLine($"[Server][{clientInfo.FolderPathToWatch}] Directory not found: {oldRelativePath}");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Server][{clientInfo.FolderPathToWatch}] Error renaming directory: {ex.Message}");
            }
        }
        else
        {
            Console.WriteLine($"[Server][{clientInfo.FolderPathToWatch}] Invalid RENAME_DIRECTORY command format.");
        }
    }

    private static void HandleDeleteDirectory(string commandArgs, ClientInfo clientInfo)
    {
        // Regular expression to match a quoted directory path
        Regex regex = new Regex("^\"([^\"]+)\"$");
        Match match = regex.Match(commandArgs);
        if (match.Success)
        {
            string relativePath = match.Groups[1].Value;
            string directoryPath = Path.Combine(clientInfo.FolderPathToWatch, relativePath);

            try
            {
                if (Directory.Exists(directoryPath))
                {
                    Directory.Delete(directoryPath, true);
                    Console.WriteLine($"[Server][{clientInfo.FolderPathToWatch}] Directory deleted: {directoryPath}");
                }
                else
                {
                    Console.WriteLine($"[Server][{clientInfo.FolderPathToWatch}] Directory not found: {directoryPath}");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Server][{clientInfo.FolderPathToWatch}] Error deleting directory: {ex.Message}");
            }
        }
        else
        {
            Console.WriteLine($"[Server][{clientInfo.FolderPathToWatch}] Invalid DELETE_DIRECTORY command format.");
        }
    }

    private static string ReadLineFromStream(NetworkStream stream)
    {
        StringBuilder sb = new StringBuilder();
        byte[] buffer = new byte[1];
        while (true)
        {
            int bytesRead = stream.Read(buffer, 0, buffer.Length);
            if (bytesRead == 0)
            {
                // Connection closed
                break;
            }
            char ch = (char)buffer[0];
            if (ch == '\n')
            {
                break;
            }
            else if (ch != '\r') // Ignore carriage return
            {
                sb.Append(ch);
            }
        }
        return sb.ToString();
    }
}
