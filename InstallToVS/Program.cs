using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace InstallToVS
{
    public class FileUnblocker
    {
        [DllImport("kernel32", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool DeleteFile(string name);

        public bool Unblock(string fileName)
        {
            return DeleteFile(fileName + ":Zone.Identifier");
        }
    }

    class Program
    {
        private const string DllName1     = "TRUDUtilsD365.dll";
        private const string DllName2     = "TRUDUtilsD365.pdb";
        private const string SettingsName = "TRUDUtilsD365Settings.xml";

        private const string AddinFolder = "AddinExtensions";

        // Any file starting with this proves an "...\Extensions\<id>\AddinExtensions"
        // folder belongs to the Dynamics 365 F&O development tools.
        private const string D365Marker = "Microsoft.Dynamics.Framework.Tools";

        // Source binaries sit next to this executable - resolve against its own
        // directory, not the (possibly different) current working directory.
        private static readonly string InstallerDir = AppDomain.CurrentDomain.BaseDirectory;

        // Exit codes (so scripted / /silent callers can detect failures):
        //   0 = installed into every target
        //   1 = fatal error (nothing found, or unhandled exception)
        //   2 = partial success (installed into some but not all targets)
        private const int ExitSuccess = 0;
        private const int ExitError   = 1;
        private const int ExitPartial = 2;

        static int Main(string[] args)
        {
            bool silent = args.Any(a =>
                a.Equals("/silent", StringComparison.OrdinalIgnoreCase) ||
                a.Equals("-silent", StringComparison.OrdinalIgnoreCase));

            int exitCode = ExitSuccess;

            try
            {
                List<string> targets = FindExtensionFolders().ToList();

                if (targets.Count == 0)
                {
                    throw new ApplicationException(
                        "Could not find any Visual Studio instance with the Dynamics 365 F&O development tools installed.");
                }

                Console.WriteLine($"Found {targets.Count} target folder(s):");
                foreach (string t in targets)
                {
                    Console.WriteLine("  " + t);
                }

                int installed = 0;
                foreach (string folder in targets)
                {
                    try
                    {
                        InstallToFolder(folder);
                        installed++;
                        Console.WriteLine($"  [OK]   {folder}");
                    }
                    catch (Exception ex)
                    {
                        Console.Error.WriteLine($"  [FAIL] {folder} : {ex.Message}");
                    }
                }

                Console.WriteLine($"Setup finished! Installed to {installed}/{targets.Count} folder(s). Close and enjoy!");

                if (installed == 0)
                {
                    exitCode = ExitError;
                }
                else if (installed < targets.Count)
                {
                    exitCode = ExitPartial;
                }
            }
            catch (Exception ee)
            {
                Console.Error.WriteLine(ee);
                Console.Error.WriteLine("Seems that an issue prevented me from doing my job :(");
                exitCode = ExitError;
            }

            if (!silent)
            {
                Console.ReadLine();
            }

            return exitCode;
        }

        private static void InstallToFolder(string extensionFolder)
        {
            // Guard against creating stray folders (e.g. when DynamicsVSTools points somewhere
            // invalid): the parent VS extension folder must already exist before we create
            // the AddinExtensions subfolder under it.
            string parent = Path.GetDirectoryName(extensionFolder);
            if (string.IsNullOrEmpty(parent) || !Directory.Exists(parent))
            {
                throw new DirectoryNotFoundException($"Parent folder does not exist: {parent}");
            }

            FileUnblocker unblocker = new FileUnblocker();
            Directory.CreateDirectory(extensionFolder);

            CopyIfPresent(unblocker, DllName1, extensionFolder, required: true);
            CopyIfPresent(unblocker, DllName2, extensionFolder, required: false);
            CopyIfPresent(unblocker, SettingsName, extensionFolder, required: false);
        }

        private static void CopyIfPresent(FileUnblocker unblocker, string fileName, string extensionFolder, bool required)
        {
            string sourcePath = Path.Combine(InstallerDir, fileName);
            if (!File.Exists(sourcePath))
            {
                if (required)
                {
                    throw new FileNotFoundException($"Source file not found next to the installer: {sourcePath}");
                }
                return;
            }

            unblocker.Unblock(sourcePath);
            File.Copy(sourcePath, Path.Combine(extensionFolder, fileName), true);
        }

        /// <summary>
        /// Returns every "...\Extensions\&lt;id&gt;\AddinExtensions" folder that belongs to a
        /// Visual Studio instance with the D365 F&amp;O dev tools installed. Version-, edition-
        /// and drive-agnostic. Results are de-duplicated (case-insensitive).
        /// </summary>
        private static IEnumerable<string> FindExtensionFolders()
        {
            HashSet<string> result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            // 1) Explicit override (kept for backwards compatibility). Validated by the same
            //    D365 marker as every other source, so we never install into a folder that
            //    does not actually host the dev tools.
            string env = Environment.GetEnvironmentVariable("DynamicsVSTools");
            if (!string.IsNullOrEmpty(env))
            {
                AddFolder(result, Path.Combine(env, AddinFolder), validate: true);
            }

            // 2) Every VS instance reported by vswhere (any drive / edition / version).
            foreach (string vsRoot in GetVsRootsFromVsWhere())
            {
                foreach (string folder in FindAddinFoldersUnderVsRoot(vsRoot))
                {
                    AddFolder(result, folder, validate: true);
                }
            }

            // 3) Filesystem fallback across the standard VS roots (covers a missing vswhere).
            foreach (string vsRoot in GetDefaultVsRoots())
            {
                foreach (string folder in FindAddinFoldersUnderVsRoot(vsRoot))
                {
                    AddFolder(result, folder, validate: true);
                }
            }

            // 4) Registry hint from the dynamics:// protocol handler (one VS install).
            foreach (string folder in GetFolderFromRegistry())
            {
                AddFolder(result, folder, validate: true);
            }

            return result;
        }

        private static void AddFolder(HashSet<string> set, string addinFolder, bool validate)
        {
            if (string.IsNullOrEmpty(addinFolder))
            {
                return;
            }
            if (validate && !IsD365AddinFolder(addinFolder))
            {
                return;
            }

            try
            {
                set.Add(Path.GetFullPath(addinFolder).TrimEnd('\\'));
            }
            catch
            {
                // ignore malformed paths
            }
        }

        private static bool IsD365AddinFolder(string addinFolder)
        {
            try
            {
                return Directory.Exists(addinFolder)
                    && Directory.EnumerateFiles(addinFolder, D365Marker + "*.dll").Any();
            }
            catch
            {
                return false;
            }
        }

        // <vsRoot>\Common7\IDE\Extensions\<id>\AddinExtensions
        private static IEnumerable<string> FindAddinFoldersUnderVsRoot(string vsRoot)
        {
            if (string.IsNullOrEmpty(vsRoot))
            {
                yield break;
            }

            string extensionsRoot = Path.Combine(vsRoot, @"Common7\IDE\Extensions");
            if (!Directory.Exists(extensionsRoot))
            {
                yield break;
            }

            IEnumerable<string> extensionDirs;
            try
            {
                extensionDirs = Directory.EnumerateDirectories(extensionsRoot);
            }
            catch
            {
                yield break;
            }

            foreach (string dir in extensionDirs)
            {
                string candidate = Path.Combine(dir, AddinFolder);
                if (IsD365AddinFolder(candidate))
                {
                    yield return candidate;
                }
            }
        }

        private static IEnumerable<string> GetVsRootsFromVsWhere()
        {
            string programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
            if (string.IsNullOrEmpty(programFilesX86))
            {
                yield break;
            }

            string vswhere = Path.Combine(programFilesX86, @"Microsoft Visual Studio\Installer\vswhere.exe");
            if (!File.Exists(vswhere))
            {
                yield break;
            }

            string output;
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo
                {
                    FileName               = vswhere,
                    Arguments              = "-all -prerelease -products * -property installationPath",
                    RedirectStandardOutput = true,
                    UseShellExecute        = false,
                    CreateNoWindow         = true
                };

                using (Process p = Process.Start(psi))
                {
                    if (p == null)
                    {
                        // No process started - fall back to the other discovery methods.
                        throw new InvalidOperationException("vswhere did not start.");
                    }
                    output = p.StandardOutput.ReadToEnd();
                    p.WaitForExit();
                }
            }
            catch
            {
                yield break;
            }

            foreach (string line in output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                string root = line.Trim();
                if (root.Length > 0)
                {
                    yield return root;
                }
            }
        }

        private static IEnumerable<string> GetDefaultVsRoots()
        {
            string[] programFiles =
            {
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86)
            };

            foreach (string pf in programFiles.Where(p => !string.IsNullOrEmpty(p)).Distinct())
            {
                string vsBase = Path.Combine(pf, "Microsoft Visual Studio");
                if (!Directory.Exists(vsBase))
                {
                    continue;
                }

                IEnumerable<string> versionDirs;
                try
                {
                    versionDirs = Directory.EnumerateDirectories(vsBase); // e.g. 2019, 2022, 18
                }
                catch
                {
                    continue;
                }

                foreach (string versionDir in versionDirs)
                {
                    IEnumerable<string> editionDirs;
                    try
                    {
                        editionDirs = Directory.EnumerateDirectories(versionDir); // Professional, Enterprise, ...
                    }
                    catch
                    {
                        continue;
                    }

                    foreach (string editionDir in editionDirs)
                    {
                        yield return editionDir;
                    }
                }
            }
        }

        // The D365 dev tools register a dynamics:// protocol handler whose command line points at
        // "...\Extensions\<id>\UrlProtocolHandler.<ver>.exe". The AddinExtensions folder sits next to it.
        private static IEnumerable<string> GetFolderFromRegistry()
        {
            string value = null;
            try
            {
                using (RegistryKey key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Classes\dynamics\shell\open\command"))
                {
                    value = key?.GetValue(null) as string;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error accessing registry: {ex.Message}");
            }

            if (string.IsNullOrEmpty(value))
            {
                yield break;
            }

            // Pull the quoted executable path, then take its directory.
            Match match = Regex.Match(value, "\"(?<exe>[^\"]+\\.exe)\"", RegexOptions.IgnoreCase);
            string exe = match.Success ? match.Groups["exe"].Value : value.Trim().Trim('"');

            string dir;
            try
            {
                dir = Path.GetDirectoryName(exe);
            }
            catch
            {
                yield break;
            }

            if (!string.IsNullOrEmpty(dir))
            {
                yield return Path.Combine(dir, AddinFolder);
            }
        }
    }
}
