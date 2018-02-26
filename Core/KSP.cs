using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using System.Transactions;
using Autofac;
using CKAN.GameVersionProviders;
using CKAN.Versioning;
using log4net;
using Newtonsoft.Json;

[assembly: InternalsVisibleTo("CKAN.Tests")]

namespace CKAN
{

    /// <summary>
    /// Everything for dealing with KSP itself.
    /// </summary>
    public class KSP : IDisposable
    {
        public IUser User { get; set; }

        #region Fields and Properties

        private static readonly ILog log = LogManager.GetLogger(typeof(KSP));

        private readonly string gameDir;
        private KspVersion version;
        private List<KspVersion> _compatibleVersions = new List<KspVersion>();

        public string Name { get; internal set; }
        public KspVersion VersionOfKspWhenCompatibleVersionsWereStored { get; private set; }
        public bool CompatibleVersionsAreFromDifferentKsp { get { return _compatibleVersions.Count > 0 && VersionOfKspWhenCompatibleVersionsWereStored != Version(); } }

        public NetModuleCache Cache { get; private set; }

        #endregion
        #region Construction and Initialisation

        /// <summary>
        /// Returns a KSP object.
        /// Will initialise a CKAN instance in the KSP dir if it does not already exist,
        /// if the directory contains a valid KSP install.
        /// </summary>
        public KSP(string gameDir, string name, IUser user)
        {
            Name = name;
            User = user;
            // Make sure our path is absolute and has normalised slashes.
            this.gameDir = KSPPathUtils.NormalizePath(Path.GetFullPath(gameDir));
            if (Valid)
            {
                SetupCkanDirectories();
                LoadCompatibleVersions();
                Cache = new NetModuleCache(DownloadCacheDir());
            }
        }

        public bool Valid { get { return IsKspDir(gameDir); } }

        /// <summary>
        /// Create the CKAN directory and any supporting files.
        /// </summary>
        private void SetupCkanDirectories()
        {
            log.InfoFormat("Initialising {0}", CkanDir());

            if (!Directory.Exists(CkanDir()))
            {
                User.RaiseMessage("Setting up CKAN for the first time...");
                User.RaiseMessage("Creating {0}", CkanDir());
                Directory.CreateDirectory(CkanDir());

                User.RaiseMessage("Scanning for installed mods...");
                ScanGameData();
            }

            if (!Directory.Exists(DownloadCacheDir()))
            {
                User.RaiseMessage("Creating {0}", DownloadCacheDir());
                Directory.CreateDirectory(DownloadCacheDir());
            }
            if (!Directory.Exists(InstallHistoryDir()))
            {
                User.RaiseMessage("Creating {0}", InstallHistoryDir());
                Directory.CreateDirectory(InstallHistoryDir());
            }

            // Clear any temporary files we find. If the directory
            // doesn't exist, then no sweat; FilesystemTransaction
            // will auto-create it as needed.
            // Create our temporary directories, or clear them if they
            // already exist.
            if (Directory.Exists(TempDir()))
            {
                var directory = new DirectoryInfo(TempDir());
                foreach (FileInfo file in directory.GetFiles())
                    file.Delete();
                foreach (DirectoryInfo subDirectory in directory.GetDirectories()) subDirectory.Delete(true);
            }
            log.InfoFormat("Initialised {0}", CkanDir());
        }

        public void SetCompatibleVersions(List<KspVersion> compatibleVersions)
        {
            this._compatibleVersions = compatibleVersions.Distinct().ToList();
            SaveCompatibleVersions();
        }

        private void SaveCompatibleVersions()
        {
            CompatibleKspVersionsDto compatibleKspVersionsDto = new CompatibleKspVersionsDto();

            compatibleKspVersionsDto.VersionOfKspWhenWritten = Version().ToString();
            compatibleKspVersionsDto.CompatibleKspVersions = _compatibleVersions.Select(v => v.ToString()).ToList();

            String json = JsonConvert.SerializeObject(compatibleKspVersionsDto);
            File.WriteAllText(CompatibleKspVersionsFile(), json);

            this.VersionOfKspWhenCompatibleVersionsWereStored = Version();
        }

        private void LoadCompatibleVersions()
        {
            String path = CompatibleKspVersionsFile();
            if (File.Exists(path))
            {
                string json = File.ReadAllText(path);
                CompatibleKspVersionsDto compatibleKspVersionsDto = JsonConvert.DeserializeObject<CompatibleKspVersionsDto>(json);

                _compatibleVersions = compatibleKspVersionsDto.CompatibleKspVersions.Select(v => KspVersion.Parse(v)).ToList();
                this.VersionOfKspWhenCompatibleVersionsWereStored = KspVersion.Parse(compatibleKspVersionsDto.VersionOfKspWhenWritten);
            }
        }

        private string CompatibleKspVersionsFile()
        {
            return Path.Combine(CkanDir(), "compatible_ksp_versions.json");
        }

        public List<KspVersion> GetCompatibleVersions()
        {
            return new List<KspVersion>(this._compatibleVersions);
        }

        #endregion

        #region Destructors and Disposal

        /// <summary>
        /// Releases all resource used by the <see cref="CKAN.KSP"/> object.
        /// </summary>
        /// <remarks>Call <see cref="Dispose"/> when you are finished using the <see cref="CKAN.KSP"/>. The <see cref="Dispose"/>
        /// method leaves the <see cref="CKAN.KSP"/> in an unusable state. After calling <see cref="Dispose"/>, you must
        /// release all references to the <see cref="CKAN.KSP"/> so the garbage collector can reclaim the memory that
        /// the <see cref="CKAN.KSP"/> was occupying.</remarks>
        public void Dispose()
        {
            if (Cache != null)
            {
                Cache.Dispose();
                Cache = null;
            }

            // Attempting to dispose of the related RegistryManager object here is a bad idea, it cause loads of failures
        }

        #endregion

        #region KSP Directory Detection and Versioning

        /// <summary>
        /// Returns the path to our portable version of KSP if ckan.exe is in the same
        /// directory as the game. Otherwise, returns null.
        /// </summary>
        public static string PortableDir()
        {
            // Find the directory our executable is stored in.
            // In Perl, this is just `use FindBin qw($Bin);` Verbose enough, C#?
            string exe_dir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);

            log.DebugFormat("Checking if KSP is in my exe dir: {0}", exe_dir);

            if (IsKspDir(exe_dir))
            {
                log.InfoFormat("KSP found at {0}", exe_dir);
                return exe_dir;
            }

            return null;
        }

        /// <summary>
        /// Attempts to automatically find a KSP install on this system.
        /// Returns the path to the install on success.
        /// Throws a DirectoryNotFoundException on failure.
        /// </summary>
        public static string FindGameDir()
        {
            // See if we can find KSP as part of a Steam install.
            string kspSteamPath = KSPPathUtils.KSPSteamPath();

            if (kspSteamPath != null)
            {
                if (IsKspDir(kspSteamPath))
                {
                    return kspSteamPath;
                }

                log.DebugFormat("Have Steam, but KSP is not at \"{0}\".", kspSteamPath);
            }

            // Oh noes! We can't find KSP!
            throw new DirectoryNotFoundException();
        }

        /// <summary>
        /// Checks if the specified directory looks like a KSP directory.
        /// Returns true if found, false if not.
        /// Checking for a GameData directory probably isn't the best way to
        /// detect KSP, but it works. More robust implementations welcome.
        /// </summary>
        internal static bool IsKspDir(string directory)
        {
            return Directory.Exists(Path.Combine(directory, "GameData"));
        }


        /// <summary>
        /// Detects the version of KSP in a given directory.
        /// Throws a NotKSPDirKraken if anything goes wrong.
        /// </summary>
        private static KspVersion DetectVersion(string directory)
        {
            KspVersion version = DetectVersionInternal(directory);

            if (version != null)
            {
                log.DebugFormat("Found version {0}", version);
                return version;
            }
            else
            {
                return null;
            }
        }

        private static KspVersion DetectVersionInternal(string directory)
        {
            var buildIdVersionProvider = ServiceLocator.Container
                .ResolveKeyed<IGameVersionProvider>(KspVersionSource.BuildId);

            KspVersion version;
            if (buildIdVersionProvider.TryGetVersion(directory, out version))
            {
                return version;
            }
            else
            {
                var readmeVersionProvider = ServiceLocator.Container
                    .ResolveKeyed<IGameVersionProvider>(KspVersionSource.Readme);

                return readmeVersionProvider.TryGetVersion(directory, out version) ? version : null;
            }
        }

        /// <summary>
        /// Rebuilds the "Ships" directory inside the current KSP instance
        /// </summary>
        public void RebuildKSPSubDir()
        {
            string[] FoldersToCheck = { "Ships/VAB", "Ships/SPH", "Ships/@thumbs/VAB", "Ships/@thumbs/SPH" };
            foreach (string sRelativePath in FoldersToCheck)
            {
                string sAbsolutePath = ToAbsoluteGameDir(sRelativePath);
                if (!Directory.Exists(sAbsolutePath))
                    Directory.CreateDirectory(sAbsolutePath);
            }
        }

        #endregion

        #region Things which would be better as Properties

        public string GameDir()
        {
            return gameDir;
        }

        public string GameData()
        {
            return KSPPathUtils.NormalizePath(
                Path.Combine(GameDir(), "GameData")
            );
        }

        public string CkanDir()
        {
            if (!Valid)
            {
                log.Error("Could not find KSP version");
                throw new NotKSPDirKraken(gameDir, "Could not find KSP version in buildID.txt or readme.txt");
            }
            return KSPPathUtils.NormalizePath(
                Path.Combine(GameDir(), "CKAN")
            );
        }

        public string DownloadCacheDir()
        {
            return KSPPathUtils.NormalizePath(
                Path.Combine(CkanDir(), "downloads")
            );
        }

        public string InstallHistoryDir()
        {
            return KSPPathUtils.NormalizePath(
                Path.Combine(CkanDir(), "history")
            );
        }

        public string Ships()
        {
            return KSPPathUtils.NormalizePath(
                Path.Combine(GameDir(), "Ships")
            );
        }

        public string ShipsVab()
        {
            return KSPPathUtils.NormalizePath(
                Path.Combine(Ships(), "VAB")
            );
        }

        public string ShipsSph()
        {
            return KSPPathUtils.NormalizePath(
                Path.Combine(Ships(), "SPH")
            );
        }

        public string ShipsThumbs()
        {
            return KSPPathUtils.NormalizePath(
                Path.Combine(Ships(), "@thumbs")
            );
        }

        public string ShipsThumbsSPH()
        {
            return KSPPathUtils.NormalizePath(
                Path.Combine(ShipsThumbs(), "SPH")
            );
        }

        public string ShipsThumbsVAB()
        {
            return KSPPathUtils.NormalizePath(
                Path.Combine(ShipsThumbs(), "VAB")
            );
        }

        public string Tutorial()
        {
            return KSPPathUtils.NormalizePath(
                Path.Combine(GameDir(), "saves", "training")
            );
        }

        public string Scenarios()
        {
            return KSPPathUtils.NormalizePath(
                Path.Combine(GameDir(), "saves", "scenarios")
            );
        }

        public string TempDir()
        {
            return KSPPathUtils.NormalizePath(
                Path.Combine(CkanDir(), "temp")
            );
        }

        public KspVersion Version()
        {
            if (version == null)
            {
                version = DetectVersion(GameDir());
            }
            return version;
        }


        public KspVersionCriteria VersionCriteria()
        {
            return new KspVersionCriteria(Version(), _compatibleVersions);
        }

        #endregion

        #region CKAN/GameData Directory Maintenance

        /// <summary>
        /// Removes all files from the download (cache) directory.
        /// </summary>
        public void CleanCache()
        {
            // TODO: We really should be asking our Cache object to do the
            // cleaning, rather than doing it ourselves.

            log.Info("Cleaning cache directory");

            string[] files = Directory.GetFiles(DownloadCacheDir(), "*", SearchOption.AllDirectories);

            foreach (string file in files)
            {
                if (Directory.Exists(file))
                {
                    log.DebugFormat("Skipping directory: {0}", file);
                    continue;
                }

                log.DebugFormat("Deleting {0}", file);
                File.Delete(file);
            }
        }

        /// <summary>
        /// Clears the registry of DLL data, and refreshes it by scanning GameData.
        /// This operates as a transaction.
        /// This *saves* the registry upon completion.
        /// </summary>
        // TODO: This would likely be better in the Registry class itself.
        public void ScanGameData()
        {
            var manager = RegistryManager.Instance(this);
            using (TransactionScope tx = CkanTransaction.CreateTransactionScope())
            {
                manager.registry.ClearDlls();

                // TODO: It would be great to optimise this to skip .git directories and the like.
                // Yes, I keep my GameData in git.

                // Alas, EnumerateFiles is *case-sensitive* in its pattern, which causes
                // DLL files to be missed under Linux; we have to pick .dll, .DLL, or scanning
                // GameData *twice*.
                //
                // The least evil is to walk it once, and filter it ourselves.
                IEnumerable<string> files = Directory.EnumerateFiles(
                                        GameData(),
                                        "*",
                                        SearchOption.AllDirectories
                                    );

                files = files.Where(file => Regex.IsMatch(file, @"\.dll$", RegexOptions.IgnoreCase));

                foreach (string dll in files.Select(KSPPathUtils.NormalizePath))
                {
                    manager.registry.RegisterDll(this, dll);
                }

                manager.ScanDlc();

                tx.Complete();
            }
            manager.Save(enforce_consistency: false);
        }

        #endregion

        /// <summary>
        /// Returns path relative to this KSP's GameDir.
        /// </summary>
        public string ToRelativeGameDir(string path)
        {
            return KSPPathUtils.ToRelative(path, GameDir());
        }

        /// <summary>
        /// Given a path relative to this KSP's GameDir, returns the
        /// absolute path on the system.
        /// </summary>
        public string ToAbsoluteGameDir(string path)
        {
            return KSPPathUtils.ToAbsolute(path, GameDir());
        }

        public override string ToString()
        {
            return "KSP Install: " + gameDir;
        }

        public override bool Equals(object obj)
        {
            var other = obj as KSP;
            return other != null ? gameDir.Equals(other.GameDir()) : base.Equals(obj);
        }

        public override int GetHashCode()
        {
            return gameDir.GetHashCode();
        }
    }

}
