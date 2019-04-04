// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

/// <summary>
/// A set of helper to manipulate paths into a canonicalized form to ensure user-provided paths
/// match those in the ETW log.
/// </summary>
static class PathExtensions
{
    /// <summary>
    /// Millisecond timeout for file / directory deletion.
    /// </summary>
    const int DeletionTimeoutMilliseconds = 10000;

    /// <summary>
    /// Back-off for repeated checks for directory deletion. According to my local experience [trylek],
    /// when the directory is opened in the file explorer, the propagation typically takes 2 seconds.
    /// </summary>
    const int DirectoryDeletionBackoffMilliseconds = 500;

    internal static string ToAbsolutePath(this string argValue) => Path.GetFullPath(argValue);

    internal static string ToAbsoluteDirectoryPath(this string argValue) => argValue.ToAbsolutePath().StripTrailingDirectorySeparators();
        
    internal static string StripTrailingDirectorySeparators(this string str)
    {
        if (String.IsNullOrWhiteSpace(str))
        {
            return str;
        }

        while (str.Length > 0 && str[str.Length - 1] == Path.DirectorySeparatorChar)
        {
            str = str.Remove(str.Length - 1);
        }

        return str;
    }

    internal static void RecreateDirectory(this string path)
    {
        if (Directory.Exists(path))
        {
            Stopwatch stopwatch = new Stopwatch();
            stopwatch.Start();
            Task<bool> deleteSubtreeTask = path.DeleteSubtree();
            deleteSubtreeTask.Wait();
            if (deleteSubtreeTask.Result)
            {
                Console.WriteLine("Deleted {0} in {1} msecs", path, stopwatch.ElapsedMilliseconds);
            }
            else
            {
                throw new Exception($"Error: Could not delete output folder {path}");
            }
        }

        Directory.CreateDirectory(path);
    }

    internal static bool IsParentOf(this DirectoryInfo outputPath, DirectoryInfo inputPath)
    {
        DirectoryInfo parentInfo = inputPath.Parent;
        while (parentInfo != null)
        {
            if (parentInfo == outputPath)
                return true;

            parentInfo = parentInfo.Parent;
        }

        return false;
    }

    public static string FindFile(this string fileName, IEnumerable<string> paths)
    {
        foreach (string path in paths)
        {
            string fileOnPath = Path.Combine(path, fileName);
            if (File.Exists(fileOnPath))
            {
                return fileOnPath;
            }
        }
        return null;
    }

    /// <summary>
    /// Parallel deletion of multiple disjunct subtrees.
    /// </summary>
    /// <param name="path">List of directories to delete</param>
    /// <returns>Task returning true on success, false on failure</returns>
    public static bool DeleteSubtrees(this string[] paths)
    {
        return DeleteSubtreesAsync(paths).Result;
    }

    private static async Task<bool> DeleteSubtreesAsync(this string[] paths)
    {
        bool succeeded = true;

        var tasks = new List<Task<bool>>();
        foreach (string path in paths)
        {
            try
            {
                if (!Directory.Exists(path))
                {
                    // Non-existent folders are harmless w.r.t. deletion
                    Console.WriteLine("Skipping non-existent folder: '{0}'", path);
                }
                else
                {
                    Console.WriteLine("Deleting '{0}'", path);
                    tasks.Add(path.DeleteSubtree());
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("Error deleting '{0}': {1}", path, ex.Message);
                succeeded = false;
            }
        }

        await Task<bool>.WhenAll(tasks);

        foreach (var task in tasks)
        {
            if (!task.Result)
            {
                succeeded = false;
                break;
            }
        }
        return succeeded;
    }

    private static async Task<bool> DeleteSubtree(this string folder)
    {
        Task<bool>[] subtasks = new []
        {
            DeleteSubtreesAsync(Directory.GetDirectories(folder)),
            DeleteFiles(Directory.GetFiles(folder))
        };

        await Task<bool>.WhenAll(subtasks);
        bool succeeded = subtasks.All(subtask => subtask.Result);

        if (succeeded)
        {
            Stopwatch folderDeletion = new Stopwatch();
            folderDeletion.Start();
            while (Directory.Exists(folder))
            {
                try
                {
                    Directory.Delete(folder, recursive: false);
                }
                catch (DirectoryNotFoundException)
                {
                    // Directory not found is OK (the directory might have been deleted during the back-off delay).
                }
                catch (Exception)
                {
                    Console.WriteLine("Folder deletion failure, maybe transient ({0} msecs): '{1}'", folderDeletion.ElapsedMilliseconds, folder);
                }

                if (!Directory.Exists(folder))
                {
                    break;
                }

                if (folderDeletion.ElapsedMilliseconds > DeletionTimeoutMilliseconds)
                {
                    Console.Error.WriteLine("Timed out trying to delete directory '{0}'", folder);
                    succeeded = false;
                    break;
                }

                Thread.Sleep(DirectoryDeletionBackoffMilliseconds);
            }
        }

        return succeeded;
    }

    private static async Task<bool> DeleteFiles(string[] files)
    {
        Task<bool>[] tasks = new Task<bool>[files.Length];
        for (int i = 0; i < files.Length; i++)
        {
            int temp = i;
            tasks[i] = Task<bool>.Run(() => files[temp].DeleteFile());
        }
        await Task<bool>.WhenAll(tasks);
        return tasks.All(task => task.Result);
    }

    private static bool DeleteFile(this string file)
    {
        try
        {
            File.Delete(file);
            return true;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"{file}: {ex.Message}");
            return false;
        }
    }

    public static string[] LocateOutputFolders(string folder, bool recursive)
    {
        return Directory.GetDirectories(folder, "*.out", recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly);
    }

    public static bool DeleteOutputFolders(string folder, bool recursive)
    {
        Stopwatch stopwatch = new Stopwatch();
        stopwatch.Start();

        Console.WriteLine("Locating output {0} {1}", (recursive ? "subtree" : "folder"), folder);
        string[] outputFolders = LocateOutputFolders(folder, recursive);
        Console.WriteLine("Deleting {0} output folders", outputFolders.Length);

        if (DeleteSubtrees(outputFolders))
        {
            Console.WriteLine("Successfully deleted {0} output folders in {1} msecs", outputFolders.Length, stopwatch.ElapsedMilliseconds);
            return true;
        }
        else
        {
            Console.Error.WriteLine("Failed deleting {0} output folders in {1} msecs", outputFolders.Length, stopwatch.ElapsedMilliseconds);
            return false;
        }
    }
}