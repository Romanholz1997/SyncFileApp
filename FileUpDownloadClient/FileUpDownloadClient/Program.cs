using System;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

class TcpClientApp
{
    private static TcpClient _client;
    private static NetworkStream _stream;
    private static bool _isSyncing = false;
    private static readonly string folderPathToWatch = @"C:\Share";
    private static readonly Dictionary<string, bool> pathIsDirectory = new Dictionary<string, bool>();
    private static readonly object pathIsDirectoryLock = new object();
    private static readonly int Port = 5000;
    private static readonly string ServerAddress = "135.181.162.240";
    //private static readonly string ServerAddress = "127.0.0.1";
    static void Main()
    {
        try
        {
            // Connect to the server (replace "127.0.0.1" with server IP if different)
            _client = new TcpClient(ServerAddress, Port);
            _stream = _client.GetStream();
            Console.WriteLine("Connected to server.");

            // Start a thread to listen for server messages
            Thread listenThread = new Thread(ListenForServerMessages);
            listenThread.Start();

            // Ensure the synchronization folder exists
            if (!Directory.Exists(folderPathToWatch))
            {
                Directory.CreateDirectory(folderPathToWatch);
            }

            // Initialize FileSystemWatcher
            FileSystemWatcher watcher = new FileSystemWatcher
            {
                Path = folderPathToWatch,
                IncludeSubdirectories = true, // Monitor subdirectories
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size
            };

            // Subscribe to events
            watcher.Created += OnCreated;
            watcher.Changed += OnChanged;
            watcher.Deleted += OnDeleted;
            watcher.Renamed += OnRenamed;

            // Start watching
            watcher.EnableRaisingEvents = true;

            Console.WriteLine($"Watching for changes in {folderPathToWatch}...");
            Console.WriteLine("Press 'q' to quit the application.");

            // Keep the application running until 'q' is pressed
            while (Console.Read() != 'q') ;

            // Cleanup
            _stream.Close();
            _client.Close();
        }
        catch (SocketException ex)
        {
            Console.WriteLine($"SocketException: {ex.Message}");
        }
    }

    private static void ListenForServerMessages()
    {
        try
        {
            while (true)
            {
                string message = ReadLineFromStream(_stream);
                if (!string.IsNullOrEmpty(message))
                {
                    HandleServerMessage(message);
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error receiving message from server: {ex.Message}");
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

    private static void HandleServerMessage(string message)
    {
        Console.WriteLine($"Received message from server: {message}");

        if (message.StartsWith("DOWNLOAD_FILE "))
        {
            HandleFileDownload(message);
        }
        else if (message.StartsWith("DELETE_FILE "))
        {
            HandleFileDeletionFromServer(message);
        }
        else if (message.StartsWith("RENAME_FILE "))
        {
            HandleFileRenameFromServer(message);
        }
        else if (message.StartsWith("CREATE_DIRECTORY "))
        {
            HandleDirectoryCreationFromServer(message);
        }
        else if (message.StartsWith("DELETE_DIRECTORY "))
        {
            HandleDirectoryDeletionFromServer(message);
        }
        else if (message.StartsWith("RENAME_DIRECTORY "))
        {
            HandleDirectoryRenameFromServer(message);
        }
        else
        {
            Console.WriteLine("Unknown message from server.");
        }
    }

    private static void HandleFileDownload(string message)
    {
        // Extract the file name
        string commandArgs = message.Substring("DOWNLOAD_FILE ".Length).Trim();
        Regex regex = new Regex("^\"([^\"]+)\"$");
        Match match = regex.Match(commandArgs);
        if (match.Success)
        {
            string fileName = match.Groups[1].Value;
            Console.WriteLine($"Downloading file from server: {fileName}");

            // Read file size
            string sizeLine = ReadLineFromStream(_stream);
            if (long.TryParse(sizeLine.Trim(), out long fileSize))
            {
                // Set the syncing flag
                _isSyncing = true;
                try
                {
                    // Receive file data
                    string filePath = Path.Combine(folderPathToWatch, fileName);
                    string directoryPath = Path.GetDirectoryName(filePath);
                    if (!Directory.Exists(directoryPath))
                    {
                        Directory.CreateDirectory(directoryPath);
                    }

                    using (FileStream fs = new FileStream(filePath, FileMode.Create, FileAccess.Write))
                    {
                        byte[] buffer = new byte[4096];
                        long totalBytesRead = 0;
                        int bytesRead;
                        while (totalBytesRead < fileSize && (bytesRead = _stream.Read(buffer, 0, buffer.Length)) > 0)
                        {
                            fs.Write(buffer, 0, bytesRead);
                            totalBytesRead += bytesRead;
                        }

                        if (totalBytesRead != fileSize)
                        {
                            Console.WriteLine("Warning: Expected file size does not match the received data.");
                        }
                    }

                    Console.WriteLine($"File downloaded: {filePath}");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Error downloading file: {ex.Message}");
                }
                finally
                {
                    // Reset the syncing flag
                    _isSyncing = false;
                }
            }
            else
            {
                Console.WriteLine("Invalid file size received from server.");
            }
        }
        else
        {
            Console.WriteLine("Invalid DOWNLOAD_FILE command format from server.");
        }
    }

    private static void HandleFileDeletionFromServer(string message)
    {
        string commandArgs = message.Substring("DELETE_FILE ".Length).Trim();
        Regex regex = new Regex("^\"([^\"]+)\"$");
        Match match = regex.Match(commandArgs);
        if (match.Success)
        {
            string fileName = match.Groups[1].Value;
            string filePath = Path.Combine(folderPathToWatch, fileName);

            try
            {
                _isSyncing = true;
                if (File.Exists(filePath))
                {
                    File.Delete(filePath);
                    Console.WriteLine($"File deleted locally: {filePath}");
                }
                else
                {
                    Console.WriteLine($"File not found locally: {filePath}");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error deleting local file: {ex.Message}");
            }
            finally
            {
                _isSyncing = false;
            }
        }
        else
        {
            Console.WriteLine("Invalid DELETE_FILE command format from server.");
        }
    }

    private static void HandleFileRenameFromServer(string message)
    {
        string commandArgs = message.Substring("RENAME_FILE ".Length).Trim();
        Regex regex = new Regex("^\"([^\"]+)\"\\s+\"([^\"]+)\"$");
        Match match = regex.Match(commandArgs);
        if (match.Success)
        {
            string oldFileName = match.Groups[1].Value;
            string newFileName = match.Groups[2].Value;
            string oldFilePath = Path.Combine(folderPathToWatch, oldFileName);
            string newFilePath = Path.Combine(folderPathToWatch, newFileName);

            try
            {
                _isSyncing = true;
                if (File.Exists(oldFilePath))
                {
                    // Ensure the target directory exists
                    string newDirectoryPath = Path.GetDirectoryName(newFilePath);
                    if (!Directory.Exists(newDirectoryPath))
                    {
                        Directory.CreateDirectory(newDirectoryPath);
                    }

                    File.Move(oldFilePath, newFilePath);
                    Console.WriteLine($"File renamed locally from {oldFileName} to {newFileName}");
                }
                else
                {
                    Console.WriteLine($"File to rename not found locally: {oldFilePath}");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error renaming local file: {ex.Message}");
            }
            finally
            {
                _isSyncing = false;
            }
        }
        else
        {
            Console.WriteLine("Invalid RENAME_FILE command format from server.");
        }
    }

    private static void HandleDirectoryCreationFromServer(string message)
    {
        string commandArgs = message.Substring("CREATE_DIRECTORY ".Length).Trim();
        Regex regex = new Regex("^\"([^\"]+)\"$");
        Match match = regex.Match(commandArgs);
        if (match.Success)
        {
            string relativePath = match.Groups[1].Value;
            string directoryPath = Path.Combine(folderPathToWatch, relativePath);

            try
            {
                _isSyncing = true;
                if (!Directory.Exists(directoryPath))
                {
                    Directory.CreateDirectory(directoryPath);
                    Console.WriteLine($"Directory created locally: {directoryPath}");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error creating local directory: {ex.Message}");
            }
            finally
            {
                _isSyncing = false;
            }
        }
        else
        {
            Console.WriteLine("Invalid CREATE_DIRECTORY command format from server.");
        }
    }

    private static void HandleDirectoryDeletionFromServer(string message)
    {
        string commandArgs = message.Substring("DELETE_DIRECTORY ".Length).Trim();
        Regex regex = new Regex("^\"([^\"]+)\"$");
        Match match = regex.Match(commandArgs);
        if (match.Success)
        {
            string relativePath = match.Groups[1].Value;
            string directoryPath = Path.Combine(folderPathToWatch, relativePath);

            try
            {
                _isSyncing = true;
                if (Directory.Exists(directoryPath))
                {
                    Directory.Delete(directoryPath, true);
                    Console.WriteLine($"Directory deleted locally: {directoryPath}");
                }
                else
                {
                    Console.WriteLine($"Directory not found locally: {directoryPath}");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error deleting local directory: {ex.Message}");
            }
            finally
            {
                _isSyncing = false;
            }
        }
        else
        {
            Console.WriteLine("Invalid DELETE_DIRECTORY command format from server.");
        }
    }

    private static void HandleDirectoryRenameFromServer(string message)
    {
        string commandArgs = message.Substring("RENAME_DIRECTORY ".Length).Trim();
        Regex regex = new Regex("^\"([^\"]+)\"\\s+\"([^\"]+)\"$");
        Match match = regex.Match(commandArgs);
        if (match.Success)
        {
            string relativeOldPath = match.Groups[1].Value;
            string relativeNewPath = match.Groups[2].Value;
            string oldDirectoryPath = Path.Combine(folderPathToWatch, relativeOldPath);
            string newDirectoryPath = Path.Combine(folderPathToWatch, relativeNewPath);

            try
            {
                _isSyncing = true;
                if (Directory.Exists(oldDirectoryPath))
                {
                    Directory.Move(oldDirectoryPath, newDirectoryPath);
                    Console.WriteLine($"Directory renamed locally from {relativeOldPath} to {relativeNewPath}");
                }
                else
                {
                    Console.WriteLine($"Directory to rename not found locally: {oldDirectoryPath}");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error renaming local directory: {ex.Message}");
            }
            finally
            {
                _isSyncing = false;
            }
        }
        else
        {
            Console.WriteLine("Invalid RENAME_DIRECTORY command format from server.");
        }
    }

    private static void OnCreated(object sender, FileSystemEventArgs e)
    {
        if (_isSyncing)
            return;
        bool isDirectory = Directory.Exists(e.FullPath);
        lock (pathIsDirectoryLock)
        {
            pathIsDirectory[e.FullPath] = isDirectory;
        }
        if (isDirectory)
        {
            // Handle directory creation
            Thread createDirThread = new Thread(() => CreateDirectoryOnServer(e.FullPath));
            createDirThread.Start();
        }
        else if (File.Exists(e.FullPath))
        {
            // Handle file creation
            Thread uploadThread = new Thread(() => UploadFile(e.FullPath));
            uploadThread.Start();
        }
    }

    private static void OnChanged(object sender, FileSystemEventArgs e)
    {
        if (_isSyncing)
            return;
        if (Directory.Exists(e.FullPath))
            return; // Changed events for directories are not handled
        lock (pathIsDirectoryLock)
        {
            pathIsDirectory[e.FullPath] = false;
        }
        if (File.Exists(e.FullPath))
        {
            // Handle file change
            Thread uploadThread = new Thread(() => UploadFile(e.FullPath));
            uploadThread.Start();
        }
    }

    private static void OnDeleted(object sender, FileSystemEventArgs e)
    {
        if (_isSyncing)
            return;

        // Determine if it's a directory or file by checking if the path was a directory before deletion
        bool isDirectory = false;
        lock (pathIsDirectoryLock)
        {
            if (pathIsDirectory.TryGetValue(e.FullPath, out isDirectory))
            {
                pathIsDirectory.Remove(e.FullPath);

                // Remove all subpaths if it's a directory
                if (isDirectory)
                {
                    var keysToRemove = new List<string>();
                    foreach (var key in pathIsDirectory.Keys)
                    {
                        if (key.StartsWith(e.FullPath + Path.DirectorySeparatorChar))
                        {
                            keysToRemove.Add(key);
                        }
                    }

                    foreach (var key in keysToRemove)
                    {
                        pathIsDirectory.Remove(key);
                    }
                }
            }
            else
            {
                // Cannot determine if the deleted item was a file or directory
                Console.WriteLine($"Deleted item not found in path cache: {e.FullPath}");
            }
        }

        if (isDirectory)
        {
            // Handle directory deletion
            Thread deleteDirThread = new Thread(() => DeleteDirectoryOnServer(e.FullPath));
            deleteDirThread.Start();
        }
        else
        {
            // Handle file deletion
            Thread deleteThread = new Thread(() => HandleFileDeletion(e.FullPath));
            deleteThread.Start();
        }
    }

    private static void OnRenamed(object sender, RenamedEventArgs e)
    {
        if (_isSyncing)
            return;
        bool isDirectory = false;
        lock (pathIsDirectoryLock)
        {
            if (pathIsDirectory.TryGetValue(e.OldFullPath, out isDirectory))
            {
                pathIsDirectory.Remove(e.OldFullPath);
                pathIsDirectory[e.FullPath] = isDirectory;
            }
            else
            {
                // Item not found in the dictionary
                Console.WriteLine($"Renamed item was not found in dictionary: {e.OldFullPath}");
                return;
            }
        }
        // Determine if it's a directory or file
        if (isDirectory)
        {
            // Handle directory rename
            Thread renameDirThread = new Thread(() => RenameDirectoryOnServer(e.OldFullPath, e.FullPath));
            renameDirThread.Start();
        }
        else
        {
            // Handle file rename
            Thread renameThread = new Thread(() => HandleFileRename(e.OldFullPath, e.FullPath));
            renameThread.Start();
        }
    }

    private static void UploadFile(string filePath)
    {
        try
        {
            // Wait for the file to become available
            WaitForFileAccess(filePath);

            // Get the relative file path
            string relativePath = Path.GetRelativePath(folderPathToWatch, filePath).Replace("\\", "/");

            // Get the file size
            FileInfo fileInfo = new FileInfo(filePath);
            long fileSize = fileInfo.Length;

            // Construct the command
            string commandString = $"UPLOAD_FILE \"{relativePath}\" {fileSize}\n";
            byte[] command = Encoding.UTF8.GetBytes(commandString);
            _stream.Write(command, 0, command.Length);

            // Send the file data
            using (FileStream fs = new FileStream(filePath, FileMode.Open, FileAccess.Read))
            {
                fs.CopyTo(_stream);
            }

            Console.WriteLine($"Uploaded file: {filePath}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error uploading file: {ex.Message}");
        }
    }

    private static void HandleFileRename(string oldFilePath, string newFilePath)
    {
        try
        {
            string relativeOldPath = Path.GetRelativePath(folderPathToWatch, oldFilePath).Replace("\\", "/");
            string relativeNewPath = Path.GetRelativePath(folderPathToWatch, newFilePath).Replace("\\", "/");
            string commandString = $"RENAME_FILE \"{relativeOldPath}\" \"{relativeNewPath}\"\n";
            byte[] command = Encoding.UTF8.GetBytes(commandString);
            _stream.Write(command, 0, command.Length);

            Console.WriteLine($"Sent rename command from {relativeOldPath} to {relativeNewPath}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error handling file rename: {ex.Message}");
        }
    }

    private static void HandleFileDeletion(string filePath)
    {
        try
        {
            string relativePath = Path.GetRelativePath(folderPathToWatch, filePath).Replace("\\", "/");
            string commandString = $"DELETE_FILE \"{relativePath}\"\n";
            byte[] command = Encoding.UTF8.GetBytes(commandString);
            _stream.Write(command, 0, command.Length);

            Console.WriteLine($"Sent delete command for file: {relativePath}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error handling file deletion: {ex.Message}");
        }
    }

    private static void CreateDirectoryOnServer(string directoryPath)
    {
        try
        {
            string relativePath = Path.GetRelativePath(folderPathToWatch, directoryPath).Replace("\\", "/");
            string commandString = $"CREATE_DIRECTORY \"{relativePath}\"\n";
            byte[] command = Encoding.UTF8.GetBytes(commandString);
            _stream.Write(command, 0, command.Length);

            Console.WriteLine($"Sent create directory command: {relativePath}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error creating directory on server: {ex.Message}");
        }
    }

    private static void RenameDirectoryOnServer(string oldPath, string newPath)
    {
        try
        {
            string relativeOldPath = Path.GetRelativePath(folderPathToWatch, oldPath).Replace("\\", "/");
            string relativeNewPath = Path.GetRelativePath(folderPathToWatch, newPath).Replace("\\", "/");
            string commandString = $"RENAME_DIRECTORY \"{relativeOldPath}\" \"{relativeNewPath}\"\n";
            byte[] command = Encoding.UTF8.GetBytes(commandString);
            _stream.Write(command, 0, command.Length);

            Console.WriteLine($"Sent rename directory command from {relativeOldPath} to {relativeNewPath}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error renaming directory on server: {ex.Message}");
        }
    }

    private static void DeleteDirectoryOnServer(string directoryPath)
    {
        try
        {
            string relativePath = Path.GetRelativePath(folderPathToWatch, directoryPath).Replace("\\", "/");
            string commandString = $"DELETE_DIRECTORY \"{relativePath}\"\n";
            byte[] command = Encoding.UTF8.GetBytes(commandString);
            _stream.Write(command, 0, command.Length);

            Console.WriteLine($"Sent delete directory command: {relativePath}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error deleting directory on server: {ex.Message}");
        }
    }

    private static void WaitForFileAccess(string filePath)
    {
        // Wait until the file is accessible to avoid exceptions due to file locks
        int maxRetries = 10;
        int delay = 500; // milliseconds
        int retries = 0;
        while (retries < maxRetries)
        {
            try
            {
                using (FileStream fs = File.Open(filePath, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    // If successful, we can proceed
                    break;
                }
            }
            catch (IOException)
            {
                // File is still locked, wait and retry
                Thread.Sleep(delay);
                retries++;
            }
        }

        if (retries == maxRetries)
        {
            Console.WriteLine($"Failed to access file after {maxRetries} retries: {filePath}");
        }
    }
}
