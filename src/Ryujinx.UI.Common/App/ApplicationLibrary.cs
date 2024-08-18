using DynamicData;
using LibHac;
using LibHac.Common;
using LibHac.Fs;
using LibHac.Fs.Fsa;
using LibHac.FsSystem;
using LibHac.Ncm;
using LibHac.Ns;
using LibHac.Tools.Fs;
using LibHac.Tools.FsSystem;
using LibHac.Tools.FsSystem.NcaUtils;
using Ryujinx.Common.Configuration;
using Ryujinx.Common.Logging;
using Ryujinx.Common.Utilities;
using Ryujinx.HLE.FileSystem;
using Ryujinx.HLE.HOS.SystemState;
using Ryujinx.HLE.Loaders.Npdm;
using Ryujinx.HLE.Loaders.Processes.Extensions;
using Ryujinx.HLE.Utilities;
using Ryujinx.UI.Common.Configuration;
using Ryujinx.UI.Common.Configuration.System;
using Ryujinx.UI.Common.Helper;
using Ryujinx.UI.Common.Models;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Threading;
using ContentType = LibHac.Ncm.ContentType;
using MissingKeyException = LibHac.Common.Keys.MissingKeyException;
using Path = System.IO.Path;
using SpanHelpers = LibHac.Common.SpanHelpers;
using TimeSpan = System.TimeSpan;

namespace Ryujinx.UI.App.Common
{
    public class ApplicationLibrary
    {
        public Language DesiredLanguage { get; set; }
        public event EventHandler<ApplicationAddedEventArgs> ApplicationAdded;
        public event EventHandler<ApplicationCountUpdatedEventArgs> ApplicationCountUpdated;

        public IObservableCache<ApplicationData, string> Applications;
        public IObservableCache<(TitleUpdateModel TitleUpdate, bool IsSelected), TitleUpdateModel> TitleUpdates;
        public IObservableCache<(DownloadableContentModel Dlc, bool IsEnabled), DownloadableContentModel> DownloadableContents;

        private readonly byte[] _nspIcon;
        private readonly byte[] _xciIcon;
        private readonly byte[] _ncaIcon;
        private readonly byte[] _nroIcon;
        private readonly byte[] _nsoIcon;

        private readonly VirtualFileSystem _virtualFileSystem;
        private readonly IntegrityCheckLevel _checkLevel;
        private CancellationTokenSource _cancellationToken;
        private readonly SourceCache<ApplicationData, string> _applications = new(it => it.Path);
        private readonly SourceCache<(TitleUpdateModel TitleUpdate, bool IsSelected), TitleUpdateModel> _titleUpdates = new(it => it.TitleUpdate);
        private readonly SourceCache<(DownloadableContentModel Dlc, bool IsEnabled), DownloadableContentModel> _downloadableContents = new(it => it.Dlc);

        private static readonly ApplicationJsonSerializerContext _serializerContext = new(JsonHelper.GetDefaultSerializerOptions());

        public ApplicationLibrary(VirtualFileSystem virtualFileSystem, IntegrityCheckLevel checkLevel)
        {
            _virtualFileSystem = virtualFileSystem;
            _checkLevel = checkLevel;

            Applications = _applications.AsObservableCache();
            TitleUpdates = _titleUpdates.AsObservableCache();
            DownloadableContents = _downloadableContents.AsObservableCache();

            _nspIcon = GetResourceBytes("Ryujinx.UI.Common.Resources.Icon_NSP.png");
            _xciIcon = GetResourceBytes("Ryujinx.UI.Common.Resources.Icon_XCI.png");
            _ncaIcon = GetResourceBytes("Ryujinx.UI.Common.Resources.Icon_NCA.png");
            _nroIcon = GetResourceBytes("Ryujinx.UI.Common.Resources.Icon_NRO.png");
            _nsoIcon = GetResourceBytes("Ryujinx.UI.Common.Resources.Icon_NSO.png");
        }

        private static byte[] GetResourceBytes(string resourceName)
        {
            Stream resourceStream = Assembly.GetCallingAssembly().GetManifestResourceStream(resourceName);
            byte[] resourceByteArray = new byte[resourceStream.Length];

            resourceStream.ReadExactly(resourceByteArray);

            return resourceByteArray;
        }

        /// <exception cref="Ryujinx.HLE.Exceptions.InvalidNpdmException">The npdm file doesn't contain valid data.</exception>
        /// <exception cref="NotImplementedException">The FsAccessHeader.ContentOwnerId section is not implemented.</exception>
        /// <exception cref="ArgumentException">An error occured while reading bytes from the stream.</exception>
        /// <exception cref="EndOfStreamException">The end of the stream is reached.</exception>
        /// <exception cref="IOException">An I/O error occurred.</exception>
        private ApplicationData GetApplicationFromExeFs(PartitionFileSystem pfs, string filePath)
        {
            ApplicationData data = new()
            {
                Icon = _nspIcon,
                Path = filePath,
            };

            using UniqueRef<IFile> npdmFile = new();

            Result result = pfs.OpenFile(ref npdmFile.Ref, "/main.npdm".ToU8Span(), OpenMode.Read);

            if (ResultFs.PathNotFound.Includes(result))
            {
                Npdm npdm = new(npdmFile.Get.AsStream());

                data.Name = npdm.TitleName;
                data.Id = npdm.Aci0.TitleId;
            }

            return data;
        }

        /// <exception cref="LibHac.Common.Keys.MissingKeyException">The configured key set is missing a key.</exception>
        /// <exception cref="InvalidDataException">The NCA header could not be decrypted.</exception>
        /// <exception cref="NotSupportedException">The NCA version is not supported.</exception>
        /// <exception cref="HorizonResultException">An error occured while reading PFS data.</exception>
        /// <exception cref="Ryujinx.HLE.Exceptions.InvalidNpdmException">The npdm file doesn't contain valid data.</exception>
        /// <exception cref="NotImplementedException">The FsAccessHeader.ContentOwnerId section is not implemented.</exception>
        /// <exception cref="ArgumentException">An error occured while reading bytes from the stream.</exception>
        /// <exception cref="EndOfStreamException">The end of the stream is reached.</exception>
        /// <exception cref="IOException">An I/O error occurred.</exception>
        private ApplicationData GetApplicationFromNsp(PartitionFileSystem pfs, string filePath)
        {
            bool isExeFs = false;

            // If the NSP doesn't have a main NCA, decrement the number of applications found and then continue to the next application.
            bool hasMainNca = false;

            foreach (DirectoryEntryEx fileEntry in pfs.EnumerateEntries("/", "*"))
            {
                if (Path.GetExtension(fileEntry.FullPath)?.ToLower() == ".nca")
                {
                    using UniqueRef<IFile> ncaFile = new();

                    try
                    {
                        pfs.OpenFile(ref ncaFile.Ref, fileEntry.FullPath.ToU8Span(), OpenMode.Read).ThrowIfFailure();

                        Nca nca = new(_virtualFileSystem.KeySet, ncaFile.Get.AsStorage());
                        int dataIndex = Nca.GetSectionIndexFromType(NcaSectionType.Data, NcaContentType.Program);

                        // Some main NCAs don't have a data partition, so check if the partition exists before opening it
                        if (nca.Header.ContentType == NcaContentType.Program &&
                            !(nca.SectionExists(NcaSectionType.Data) &&
                              nca.Header.GetFsHeader(dataIndex).IsPatchSection()))
                        {
                            hasMainNca = true;

                            break;
                        }
                    }
                    catch (Exception exception)
                    {
                        Logger.Warning?.Print(LogClass.Application, $"Encountered an error while trying to load applications from file '{filePath}': {exception}");

                        return null;
                    }
                }
                else if (Path.GetFileNameWithoutExtension(fileEntry.FullPath) == "main")
                {
                    isExeFs = true;
                }
            }

            if (hasMainNca)
            {
                List<ApplicationData> applications = GetApplicationsFromPfs(pfs, filePath);

                switch (applications.Count)
                {
                    case 1:
                        return applications[0];
                    case >= 1:
                        Logger.Warning?.Print(LogClass.Application, $"File '{filePath}' contains more applications than expected: {applications.Count}");
                        return applications[0];
                    default:
                        return null;
                }
            }

            if (isExeFs)
            {
                return GetApplicationFromExeFs(pfs, filePath);
            }

            return null;
        }

        /// <exception cref="LibHac.Common.Keys.MissingKeyException">The configured key set is missing a key.</exception>
        /// <exception cref="InvalidDataException">The NCA header could not be decrypted.</exception>
        /// <exception cref="NotSupportedException">The NCA version is not supported.</exception>
        /// <exception cref="HorizonResultException">An error occured while reading PFS data.</exception>
        private List<ApplicationData> GetApplicationsFromPfs(IFileSystem pfs, string filePath)
        {
            var applications = new List<ApplicationData>();
            string extension = Path.GetExtension(filePath).ToLower();

            foreach ((ulong titleId, ContentMetaData content) in pfs.GetContentData(ContentMetaType.Application, _virtualFileSystem, _checkLevel))
            {
                ApplicationData applicationData = new()
                {
                    Id = titleId,
                    Path = filePath,
                };

                Nca mainNca = content.GetNcaByType(_virtualFileSystem.KeySet, ContentType.Program);
                Nca controlNca = content.GetNcaByType(_virtualFileSystem.KeySet, ContentType.Control);

                BlitStruct<ApplicationControlProperty> controlHolder = new(1);

                IFileSystem controlFs = controlNca?.OpenFileSystem(NcaSectionType.Data, _checkLevel);

                // Check if there is an update available.
                if (IsUpdateApplied(mainNca, out IFileSystem updatedControlFs))
                {
                    // Replace the original ControlFs by the updated one.
                    controlFs = updatedControlFs;
                }

                if (controlFs == null)
                {
                    continue;
                }

                ReadControlData(controlFs, controlHolder.ByteSpan);

                GetApplicationInformation(ref controlHolder.Value, ref applicationData);

                // Read the icon from the ControlFS and store it as a byte array
                try
                {
                    using UniqueRef<IFile> icon = new();

                    controlFs.OpenFile(ref icon.Ref, $"/icon_{DesiredLanguage}.dat".ToU8Span(), OpenMode.Read).ThrowIfFailure();

                    using MemoryStream stream = new();

                    icon.Get.AsStream().CopyTo(stream);
                    applicationData.Icon = stream.ToArray();
                }
                catch (HorizonResultException)
                {
                    foreach (DirectoryEntryEx entry in controlFs.EnumerateEntries("/", "*"))
                    {
                        if (entry.Name == "control.nacp")
                        {
                            continue;
                        }

                        using var icon = new UniqueRef<IFile>();

                        controlFs.OpenFile(ref icon.Ref, entry.FullPath.ToU8Span(), OpenMode.Read).ThrowIfFailure();

                        using MemoryStream stream = new();

                        icon.Get.AsStream().CopyTo(stream);
                        applicationData.Icon = stream.ToArray();

                        if (applicationData.Icon != null)
                        {
                            break;
                        }
                    }

                    applicationData.Icon ??= extension == ".xci" ? _xciIcon : _nspIcon;
                }

                applicationData.ControlHolder = controlHolder;

                applications.Add(applicationData);
            }

            return applications;
        }

        public bool TryGetApplicationsFromFile(string applicationPath, out List<ApplicationData> applications)
        {
            applications = [];
            long fileSize;

            try
            {
                fileSize = new FileInfo(applicationPath).Length;
            }
            catch (FileNotFoundException)
            {
                Logger.Warning?.Print(LogClass.Application, $"The file was not found: '{applicationPath}'");

                return false;
            }

            BlitStruct<ApplicationControlProperty> controlHolder = new(1);

            try
            {
                string extension = Path.GetExtension(applicationPath).ToLower();

                using FileStream file = new(applicationPath, FileMode.Open, FileAccess.Read);

                switch (extension)
                {
                    case ".xci":
                        {
                            Xci xci = new(_virtualFileSystem.KeySet, file.AsStorage());

                            applications = GetApplicationsFromPfs(xci.OpenPartition(XciPartitionType.Secure), applicationPath);

                            if (applications.Count == 0)
                            {
                                return false;
                            }

                            break;
                        }
                    case ".nsp":
                    case ".pfs0":
                        {
                            var pfs = new PartitionFileSystem();
                            pfs.Initialize(file.AsStorage()).ThrowIfFailure();

                            ApplicationData result = GetApplicationFromNsp(pfs, applicationPath);

                            if (result == null)
                            {
                                return false;
                            }

                            applications.Add(result);

                            break;
                        }
                    case ".nro":
                        {
                            BinaryReader reader = new(file);
                            ApplicationData application = new();

                            file.Seek(24, SeekOrigin.Begin);

                            int assetOffset = reader.ReadInt32();

                            if (Encoding.ASCII.GetString(Read(assetOffset, 4)) == "ASET")
                            {
                                byte[] iconSectionInfo = Read(assetOffset + 8, 0x10);

                                long iconOffset = BitConverter.ToInt64(iconSectionInfo, 0);
                                long iconSize = BitConverter.ToInt64(iconSectionInfo, 8);

                                ulong nacpOffset = reader.ReadUInt64();
                                ulong nacpSize = reader.ReadUInt64();

                                // Reads and stores game icon as byte array
                                if (iconSize > 0)
                                {
                                    application.Icon = Read(assetOffset + iconOffset, (int)iconSize);
                                }
                                else
                                {
                                    application.Icon = _nroIcon;
                                }

                                // Read the NACP data
                                Read(assetOffset + (int)nacpOffset, (int)nacpSize).AsSpan().CopyTo(controlHolder.ByteSpan);

                                GetApplicationInformation(ref controlHolder.Value, ref application);
                            }
                            else
                            {
                                application.Icon = _nroIcon;
                                application.Name = Path.GetFileNameWithoutExtension(applicationPath);
                            }

                            application.ControlHolder = controlHolder;
                            applications.Add(application);

                            break;

                            byte[] Read(long position, int size)
                            {
                                file.Seek(position, SeekOrigin.Begin);

                                return reader.ReadBytes(size);
                            }
                        }
                    case ".nca":
                        {
                            ApplicationData application = new();

                            Nca nca = new(_virtualFileSystem.KeySet, new FileStream(applicationPath, FileMode.Open, FileAccess.Read).AsStorage());

                            if (!nca.IsProgram() || nca.IsPatch())
                            {
                                return false;
                            }

                            application.Icon = _ncaIcon;
                            application.Name = Path.GetFileNameWithoutExtension(applicationPath);
                            application.ControlHolder = controlHolder;

                            applications.Add(application);

                            break;
                        }
                    // If its an NSO we just set defaults
                    case ".nso":
                        {
                            ApplicationData application = new()
                            {
                                Icon = _nsoIcon,
                                Name = Path.GetFileNameWithoutExtension(applicationPath),
                            };

                            applications.Add(application);

                            break;
                        }
                }
            }
            catch (MissingKeyException exception)
            {
                Logger.Warning?.Print(LogClass.Application, $"Your key set is missing a key with the name: {exception.Name}");

                return false;
            }
            catch (InvalidDataException)
            {
                Logger.Warning?.Print(LogClass.Application, $"The header key is incorrect or missing and therefore the NCA header content type check has failed. Errored File: {applicationPath}");

                return false;
            }
            catch (IOException exception)
            {
                Logger.Warning?.Print(LogClass.Application, exception.Message);

                return false;
            }
            catch (Exception exception)
            {
                Logger.Warning?.Print(LogClass.Application, $"The file encountered was not of a valid type. File: '{applicationPath}' Error: {exception}");

                return false;
            }

            foreach (var data in applications)
            {
                // Only load metadata for applications with an ID
                if (data.Id != 0)
                {
                    ApplicationMetadata appMetadata = LoadAndSaveMetaData(data.IdString, appMetadata =>
                    {
                        appMetadata.Title = data.Name;

                        // Only do the migration if time_played has a value and timespan_played hasn't been updated yet.
                        if (appMetadata.TimePlayedOld != default && appMetadata.TimePlayed == TimeSpan.Zero)
                        {
                            appMetadata.TimePlayed = TimeSpan.FromSeconds(appMetadata.TimePlayedOld);
                            appMetadata.TimePlayedOld = default;
                        }

                        // Only do the migration if last_played has a value and last_played_utc doesn't exist yet.
                        if (appMetadata.LastPlayedOld != default && !appMetadata.LastPlayed.HasValue)
                        {
                            // Migrate from string-based last_played to DateTime-based last_played_utc.
                            if (DateTime.TryParse(appMetadata.LastPlayedOld, out DateTime lastPlayedOldParsed))
                            {
                                appMetadata.LastPlayed = lastPlayedOldParsed;

                                // Migration successful: deleting last_played from the metadata file.
                                appMetadata.LastPlayedOld = default;
                            }

                        }
                    });

                    data.Favorite = appMetadata.Favorite;
                    data.TimePlayed = appMetadata.TimePlayed;
                    data.LastPlayed = appMetadata.LastPlayed;
                }

                data.FileExtension = Path.GetExtension(applicationPath).TrimStart('.').ToUpper();
                data.FileSize = fileSize;
                data.Path = applicationPath;
            }

            return true;
        }

        public bool TryGetDownloadableContentFromFile(string filePath, out List<DownloadableContentModel> titleUpdates)
        {
            titleUpdates = [];

            try
            {
                string extension = Path.GetExtension(filePath).ToLower();

                using FileStream file = new(filePath, FileMode.Open, FileAccess.Read);

                switch (extension)
                {
                    case ".xci":
                    case ".nsp":
                        {
                            IntegrityCheckLevel checkLevel = ConfigurationState.Instance.System.EnableFsIntegrityChecks
                                ? IntegrityCheckLevel.ErrorOnInvalid
                                : IntegrityCheckLevel.None;

                            using IFileSystem pfs = PartitionFileSystemUtils.OpenApplicationFileSystem(filePath, _virtualFileSystem);

                            foreach (DirectoryEntryEx fileEntry in pfs.EnumerateEntries("/", "*.nca"))
                            {
                                using var ncaFile = new UniqueRef<IFile>();

                                pfs.OpenFile(ref ncaFile.Ref, fileEntry.FullPath.ToU8Span(), OpenMode.Read).ThrowIfFailure();

                                Nca nca = TryOpenNca(ncaFile.Get.AsStorage());
                                if (nca == null)
                                {
                                    continue;
                                }

                                if (nca.Header.ContentType == NcaContentType.PublicData)
                                {
                                    titleUpdates.Add(new DownloadableContentModel(nca.Header.TitleId, filePath, fileEntry.FullPath));
                                }
                            }

                            return titleUpdates.Count != 0;
                        }
                }
            }
            catch (MissingKeyException exception)
            {
                Logger.Warning?.Print(LogClass.Application, $"Your key set is missing a key with the name: {exception.Name}");
            }
            catch (InvalidDataException)
            {
                Logger.Warning?.Print(LogClass.Application, $"The header key is incorrect or missing and therefore the NCA header content type check has failed. Errored File: {filePath}");
            }
            catch (IOException exception)
            {
                Logger.Warning?.Print(LogClass.Application, exception.Message);
            }
            catch (Exception exception)
            {
                Logger.Warning?.Print(LogClass.Application, $"The file encountered was not of a valid type. File: '{filePath}' Error: {exception}");
            }
            
            return false;
        }

        public bool TryGetTitleUpdatesFromFile(string filePath, out List<TitleUpdateModel> titleUpdates)
        {
            titleUpdates = [];

            try
            {
                string extension = Path.GetExtension(filePath).ToLower();

                using FileStream file = new(filePath, FileMode.Open, FileAccess.Read);

                switch (extension)
                {
                    case ".xci":
                    case ".nsp":
                        {
                            IntegrityCheckLevel checkLevel = ConfigurationState.Instance.System.EnableFsIntegrityChecks
                                ? IntegrityCheckLevel.ErrorOnInvalid
                                : IntegrityCheckLevel.None;

                            using IFileSystem pfs =
                                PartitionFileSystemUtils.OpenApplicationFileSystem(filePath, _virtualFileSystem);

                            Dictionary<ulong, ContentMetaData> updates =
                                pfs.GetContentData(ContentMetaType.Patch, _virtualFileSystem, checkLevel);

                            if (updates.Count == 0)
                            {
                                return false;
                            }

                            foreach ((_, ContentMetaData content) in updates)
                            {
                                Nca patchNca = content.GetNcaByType(_virtualFileSystem.KeySet, ContentType.Program);
                                Nca controlNca = content.GetNcaByType(_virtualFileSystem.KeySet, ContentType.Control);

                                if (controlNca != null && patchNca != null)
                                {
                                    ApplicationControlProperty controlData = new();

                                    using UniqueRef<IFile> nacpFile = new();

                                    controlNca.OpenFileSystem(NcaSectionType.Data, IntegrityCheckLevel.None)
                                        .OpenFile(ref nacpFile.Ref, "/control.nacp".ToU8Span(), OpenMode.Read)
                                        .ThrowIfFailure();
                                    nacpFile.Get.Read(out _, 0, SpanHelpers.AsByteSpan(ref controlData),
                                        ReadOption.None).ThrowIfFailure();

                                    var displayVersion = controlData.DisplayVersionString.ToString();
                                    var update = new TitleUpdateModel(content.ApplicationId, content.Version.Version,
                                        displayVersion, filePath);

                                    titleUpdates.Add(update);
                                }
                            }

                            return true;
                        }
                }
            }
            catch (MissingKeyException exception)
            {
                Logger.Warning?.Print(LogClass.Application, $"Your key set is missing a key with the name: {exception.Name}");
            }
            catch (InvalidDataException)
            {
                Logger.Warning?.Print(LogClass.Application, $"The header key is incorrect or missing and therefore the NCA header content type check has failed. Errored File: {filePath}");
            }
            catch (IOException exception)
            {
                Logger.Warning?.Print(LogClass.Application, exception.Message);
            }
            catch (Exception exception)
            {
                Logger.Warning?.Print(LogClass.Application, $"The file encountered was not of a valid type. File: '{filePath}' Error: {exception}");
            }
            return false;
        }

        public void CancelLoading()
        {
            _cancellationToken?.Cancel();
        }

        public static void ReadControlData(IFileSystem controlFs, Span<byte> outProperty)
        {
            using UniqueRef<IFile> controlFile = new();

            controlFs.OpenFile(ref controlFile.Ref, "/control.nacp".ToU8Span(), OpenMode.Read).ThrowIfFailure();
            controlFile.Get.Read(out _, 0, outProperty, ReadOption.None).ThrowIfFailure();
        }

        public void LoadApplications(List<string> appDirs)
        {
            int numApplicationsFound = 0;
            int numApplicationsLoaded = 0;

            _cancellationToken = new CancellationTokenSource();
            _applications.Clear();

            // Builds the applications list with paths to found applications
            List<string> applicationPaths = new();

            try
            {
                foreach (string appDir in appDirs)
                {
                    if (_cancellationToken.Token.IsCancellationRequested)
                    {
                        return;
                    }

                    if (!Directory.Exists(appDir))
                    {
                        Logger.Warning?.Print(LogClass.Application, $"The specified game directory \"{appDir}\" does not exist.");

                        continue;
                    }

                    try
                    {
                        EnumerationOptions options = new()
                        {
                            RecurseSubdirectories = true,
                            IgnoreInaccessible = false,
                        };

                        IEnumerable<string> files = Directory.EnumerateFiles(appDir, "*", options).Where(file =>
                        {
                            return
                                (Path.GetExtension(file).ToLower() is ".nsp" && ConfigurationState.Instance.UI.ShownFileTypes.NSP.Value) ||
                                (Path.GetExtension(file).ToLower() is ".pfs0" && ConfigurationState.Instance.UI.ShownFileTypes.PFS0.Value) ||
                                (Path.GetExtension(file).ToLower() is ".xci" && ConfigurationState.Instance.UI.ShownFileTypes.XCI.Value) ||
                                (Path.GetExtension(file).ToLower() is ".nca" && ConfigurationState.Instance.UI.ShownFileTypes.NCA.Value) ||
                                (Path.GetExtension(file).ToLower() is ".nro" && ConfigurationState.Instance.UI.ShownFileTypes.NRO.Value) ||
                                (Path.GetExtension(file).ToLower() is ".nso" && ConfigurationState.Instance.UI.ShownFileTypes.NSO.Value);
                        });

                        foreach (string app in files)
                        {
                            if (_cancellationToken.Token.IsCancellationRequested)
                            {
                                return;
                            }

                            var fileInfo = new FileInfo(app);

                            try
                            {
                                var fullPath = fileInfo.ResolveLinkTarget(true)?.FullName ?? fileInfo.FullName;

                                applicationPaths.Add(fullPath);
                                numApplicationsFound++;
                            }
                            catch (IOException exception)
                            {
                                Logger.Warning?.Print(LogClass.Application, $"Failed to resolve the full path to file: \"{app}\" Error: {exception}");
                            }
                        }
                    }
                    catch (UnauthorizedAccessException)
                    {
                        Logger.Warning?.Print(LogClass.Application, $"Failed to get access to directory: \"{appDir}\"");
                    }
                }

                // Loops through applications list, creating a struct and then firing an event containing the struct for each application
                foreach (string applicationPath in applicationPaths)
                {
                    if (_cancellationToken.Token.IsCancellationRequested)
                    {
                        return;
                    }

                    if (TryGetApplicationsFromFile(applicationPath, out List<ApplicationData> applications))
                    {
                        foreach (var application in applications)
                        {
                            OnApplicationAdded(new ApplicationAddedEventArgs
                            {
                                AppData = application,
                            });
                        }

                        _applications.Edit(it =>
                        {
                            foreach (var application in applications)
                            {
                                it.AddOrUpdate(application);
                            }
                        });

                        if (applications.Count > 1)
                        {
                            numApplicationsFound += applications.Count - 1;
                        }

                        numApplicationsLoaded += applications.Count;
                    }
                    else
                    {
                        numApplicationsFound--;
                    }

                    OnApplicationCountUpdated(new ApplicationCountUpdatedEventArgs
                    {
                        NumAppsFound = numApplicationsFound,
                        NumAppsLoaded = numApplicationsLoaded,
                    });
                }

                OnApplicationCountUpdated(new ApplicationCountUpdatedEventArgs
                {
                    NumAppsFound = numApplicationsFound,
                    NumAppsLoaded = numApplicationsLoaded,
                });
            }
            finally
            {
                _cancellationToken.Dispose();
                _cancellationToken = null;
            }
        }

        public void LoadTitleUpdates()
        {
            return;
        }

        public void LoadDownloadableContents()
        {
            _downloadableContents.Edit(it =>
            {
                it.Clear();

                foreach (ApplicationData application in Applications.Items)
                {
                    var savedDlc = DownloadableContentsHelper.LoadDownloadableContentsJson(_virtualFileSystem, application.IdBase);
                    it.AddOrUpdate(savedDlc);

                    if (TryGetDownloadableContentFromFile(application.Path, out var bundledDlc))
                    {
                        var savedDlcLookup = savedDlc.Select(dlc => dlc.Item1).ToHashSet();

                        bool addedNewDlc = false;
                        foreach (var dlc in bundledDlc)
                        {
                            if (!savedDlcLookup.Contains(dlc))
                            {
                                addedNewDlc = true;
                                it.AddOrUpdate((dlc, true));
                            }
                        }

                        if (addedNewDlc)
                        {
                            var gameDlcs = it.Items.Where(dlc => dlc.Dlc.TitleIdBase == application.IdBase).ToList();
                            DownloadableContentsHelper.SaveDownloadableContentsJson(_virtualFileSystem, application.IdBase, gameDlcs);
                        }
                    }
                }
            });
        }

        private void SaveDownloadableContentsForGame(ulong titleIdBase)
        {
            var dlcs = DownloadableContents.Items.Where(dlc => dlc.Dlc.TitleIdBase == titleIdBase).ToList();
            DownloadableContentsHelper.SaveDownloadableContentsJson(_virtualFileSystem, titleIdBase, dlcs);
        }

        public void SaveDownloadableContentsForGame(ApplicationData application, List<(DownloadableContentModel, bool IsEnabled)> dlcs)
        {
            _downloadableContents.Edit(it =>
            {
                DownloadableContentsHelper.SaveDownloadableContentsJson(_virtualFileSystem, application.IdBase, dlcs);

                it.Remove(it.Items.Where(item => item.Dlc.TitleIdBase == application.IdBase));
                it.AddOrUpdate(dlcs);
            });
        }

        public int AutoLoadDownloadableContents(List<string> appDirs)
        {
            _cancellationToken = new CancellationTokenSource();

            List<string> dlcPaths = new();
            int newDlcLoaded = 0;

            try
            {
                foreach (string appDir in appDirs)
                {
                    if (_cancellationToken.Token.IsCancellationRequested)
                    {
                        return newDlcLoaded;
                    }

                    if (!Directory.Exists(appDir))
                    {
                        Logger.Warning?.Print(LogClass.Application,
                            $"The specified game directory \"{appDir}\" does not exist.");

                        continue;
                    }

                    try
                    {
                        EnumerationOptions options = new()
                        {
                            RecurseSubdirectories = true,
                            IgnoreInaccessible = false,
                        };

                        IEnumerable<string> files = Directory.EnumerateFiles(appDir, "*", options).Where(
                            file =>
                            {
                                return
                                    (Path.GetExtension(file).ToLower() is ".nsp" &&
                                     ConfigurationState.Instance.UI.ShownFileTypes.NSP.Value);
                            });

                        foreach (string app in files)
                        {
                            if (_cancellationToken.Token.IsCancellationRequested)
                            {
                                return newDlcLoaded;
                            }

                            var fileInfo = new FileInfo(app);

                            try
                            {
                                var fullPath = fileInfo.ResolveLinkTarget(true)?.FullName ?? fileInfo.FullName;

                                dlcPaths.Add(fullPath);
                            }
                            catch (IOException exception)
                            {
                                Logger.Warning?.Print(LogClass.Application,
                                    $"Failed to resolve the full path to file: \"{app}\" Error: {exception}");
                            }
                        }
                    }
                    catch (UnauthorizedAccessException)
                    {
                        Logger.Warning?.Print(LogClass.Application,
                            $"Failed to get access to directory: \"{appDir}\"");
                    }
                }

                var appIdLookup = Applications.Items.Select(it => it.IdBase).ToHashSet();

                foreach (string dlcPath in dlcPaths)
                {
                    if (_cancellationToken.Token.IsCancellationRequested)
                    {
                        return newDlcLoaded;
                    }

                    if (TryGetDownloadableContentFromFile(dlcPath, out var foundDlcs))
                    {
                        foreach (var dlc in foundDlcs.Where(it => appIdLookup.Contains(it.TitleIdBase)))
                        {
                            if (!DownloadableContents.Items.Any(it => it.Dlc == dlc))
                            {
                                _downloadableContents.AddOrUpdate((dlc, true));
                                SaveDownloadableContentsForGame(dlc.TitleIdBase);
                                newDlcLoaded++;
                            }
                        }
                    }
                }
            }
            finally
            {
                _cancellationToken.Dispose();
                _cancellationToken = null;
            }

            return newDlcLoaded;
        }

        public int AutoLoadTitleUpdates(List<string> appDirs)
        {
            return 0;
            //     _cancellationToken = new CancellationTokenSource();
            //     _titleUpdates.Clear();
            //
            //     // Builds the applications list with paths to found applications
            //     List<string> applicationPaths = new();
            //
            //     try
            //     {
            //         foreach (string appDir in appDirs)
            //         {
            //             if (_cancellationToken.Token.IsCancellationRequested)
            //             {
            //                 return;
            //             }
            //
            //             if (!Directory.Exists(appDir))
            //             {
            //                 Logger.Warning?.Print(LogClass.Application,
            //                     $"The specified game directory \"{appDir}\" does not exist.");
            //
            //                 continue;
            //             }
            //
            //             try
            //             {
            //                 EnumerationOptions options = new()
            //                 {
            //                     RecurseSubdirectories = true,
            //                     IgnoreInaccessible = false,
            //                 };
            //
            //                 IEnumerable<string> files = Directory.EnumerateFiles(appDir, "*", options)
            //                     .Where(file =>
            //                     {
            //                         return
            //                             (Path.GetExtension(file).ToLower() is ".nsp" &&
            //                              ConfigurationState.Instance.UI.ShownFileTypes.NSP.Value) ||
            //                             (Path.GetExtension(file).ToLower() is ".xci" &&
            //                              ConfigurationState.Instance.UI.ShownFileTypes.XCI.Value);
            //                     });
            //
            //                 foreach (string app in files)
            //                 {
            //                     if (_cancellationToken.Token.IsCancellationRequested)
            //                     {
            //                         return;
            //                     }
            //
            //                     var fileInfo = new FileInfo(app);
            //
            //                     try
            //                     {
            //                         var fullPath = fileInfo.ResolveLinkTarget(true)?.FullName ??
            //                                        fileInfo.FullName;
            //
            //                         applicationPaths.Add(fullPath);
            //                     }
            //                     catch (IOException exception)
            //                     {
            //                         Logger.Warning?.Print(LogClass.Application,
            //                             $"Failed to resolve the full path to file: \"{app}\" Error: {exception}");
            //                     }
            //                 }
            //             }
            //             catch (UnauthorizedAccessException)
            //             {
            //                 Logger.Warning?.Print(LogClass.Application,
            //                     $"Failed to get access to directory: \"{appDir}\"");
            //             }
            //         }
            //
            //         // Loops through applications list, creating a struct and then firing an event containing the struct for each application
            //         foreach (string applicationPath in applicationPaths)
            //         {
            //             if (_cancellationToken.Token.IsCancellationRequested)
            //             {
            //                 return;
            //             }
            //
            //             if (TryGetTitleUpdatesFromFile(applicationPath, out List<TitleUpdateModel> titleUpdates))
            //             {
            //                 foreach (var titleUpdate in titleUpdates)
            //                 {
            //                     OnTitleUpdateAdded(new TitleUpdateAddedEventArgs()
            //                     {
            //                         TitleUpdate = titleUpdate,
            //                     });
            //                 }
            //
            //                 _titleUpdates.Edit(it =>
            //                 {
            //                     foreach (var titleUpdate in titleUpdates)
            //                     {
            //                         it.AddOrUpdate((titleUpdate, false));
            //                     }
            //                 });
            //             }
            //         }
            //     }
            //     finally
            //     {
            //         _cancellationToken.Dispose();
            //         _cancellationToken = null;
            //     }
        }

        protected void OnApplicationAdded(ApplicationAddedEventArgs e)
        {
            ApplicationAdded?.Invoke(null, e);
        }

        protected void OnApplicationCountUpdated(ApplicationCountUpdatedEventArgs e)
        {
            ApplicationCountUpdated?.Invoke(null, e);
        }

        public static ApplicationMetadata LoadAndSaveMetaData(string titleId, Action<ApplicationMetadata> modifyFunction = null)
        {
            string metadataFolder = Path.Combine(AppDataManager.GamesDirPath, titleId, "gui");
            string metadataFile = Path.Combine(metadataFolder, "metadata.json");

            ApplicationMetadata appMetadata;

            if (!File.Exists(metadataFile))
            {
                Directory.CreateDirectory(metadataFolder);

                appMetadata = new ApplicationMetadata();

                JsonHelper.SerializeToFile(metadataFile, appMetadata, _serializerContext.ApplicationMetadata);
            }

            try
            {
                appMetadata = JsonHelper.DeserializeFromFile(metadataFile, _serializerContext.ApplicationMetadata);
            }
            catch (JsonException)
            {
                Logger.Warning?.Print(LogClass.Application, $"Failed to parse metadata json for {titleId}. Loading defaults.");

                appMetadata = new ApplicationMetadata();
            }

            if (modifyFunction != null)
            {
                modifyFunction(appMetadata);

                JsonHelper.SerializeToFile(metadataFile, appMetadata, _serializerContext.ApplicationMetadata);
            }

            return appMetadata;
        }

        public byte[] GetApplicationIcon(string applicationPath, Language desiredTitleLanguage, ulong applicationId)
        {
            byte[] applicationIcon = null;

            if (applicationId == 0)
            {
                if (Directory.Exists(applicationPath))
                {
                    return _ncaIcon;
                }

                return Path.GetExtension(applicationPath).ToLower() switch
                {
                    ".nsp" => _nspIcon,
                    ".pfs0" => _nspIcon,
                    ".xci" => _xciIcon,
                    ".nso" => _nsoIcon,
                    ".nro" => _nroIcon,
                    ".nca" => _ncaIcon,
                    _ => _ncaIcon,
                };
            }

            try
            {
                // Look for icon only if applicationPath is not a directory
                if (!Directory.Exists(applicationPath))
                {
                    string extension = Path.GetExtension(applicationPath).ToLower();

                    using FileStream file = new(applicationPath, FileMode.Open, FileAccess.Read);

                    if (extension == ".nsp" || extension == ".pfs0" || extension == ".xci")
                    {
                        try
                        {
                            IFileSystem pfs;

                            bool isExeFs = false;

                            if (extension == ".xci")
                            {
                                Xci xci = new(_virtualFileSystem.KeySet, file.AsStorage());

                                pfs = xci.OpenPartition(XciPartitionType.Secure);
                            }
                            else
                            {
                                var pfsTemp = new PartitionFileSystem();
                                pfsTemp.Initialize(file.AsStorage()).ThrowIfFailure();
                                pfs = pfsTemp;

                                foreach (DirectoryEntryEx fileEntry in pfs.EnumerateEntries("/", "*"))
                                {
                                    if (Path.GetFileNameWithoutExtension(fileEntry.FullPath) == "main")
                                    {
                                        isExeFs = true;
                                    }
                                }
                            }

                            if (isExeFs)
                            {
                                applicationIcon = _nspIcon;
                            }
                            else
                            {
                                // Store the ControlFS in variable called controlFs
                                Dictionary<ulong, ContentMetaData> programs = pfs.GetContentData(ContentMetaType.Application, _virtualFileSystem, _checkLevel);
                                IFileSystem controlFs = null;

                                if (programs.TryGetValue(applicationId, out ContentMetaData value))
                                {
                                    if (value.GetNcaByType(_virtualFileSystem.KeySet, ContentType.Control) is { } controlNca)
                                    {
                                        controlFs = controlNca.OpenFileSystem(NcaSectionType.Data, IntegrityCheckLevel.None);
                                    }
                                }

                                // Read the icon from the ControlFS and store it as a byte array
                                try
                                {
                                    using var icon = new UniqueRef<IFile>();

                                    controlFs.OpenFile(ref icon.Ref, $"/icon_{desiredTitleLanguage}.dat".ToU8Span(), OpenMode.Read).ThrowIfFailure();

                                    using MemoryStream stream = new();

                                    icon.Get.AsStream().CopyTo(stream);
                                    applicationIcon = stream.ToArray();
                                }
                                catch (HorizonResultException)
                                {
                                    foreach (DirectoryEntryEx entry in controlFs.EnumerateEntries("/", "*"))
                                    {
                                        if (entry.Name == "control.nacp")
                                        {
                                            continue;
                                        }

                                        using var icon = new UniqueRef<IFile>();

                                        controlFs.OpenFile(ref icon.Ref, entry.FullPath.ToU8Span(), OpenMode.Read).ThrowIfFailure();

                                        using MemoryStream stream = new();
                                        icon.Get.AsStream().CopyTo(stream);
                                        applicationIcon = stream.ToArray();

                                        break;
                                    }

                                    applicationIcon ??= extension == ".xci" ? _xciIcon : _nspIcon;
                                }
                            }
                        }
                        catch (MissingKeyException)
                        {
                            applicationIcon = extension == ".xci" ? _xciIcon : _nspIcon;
                        }
                        catch (InvalidDataException)
                        {
                            applicationIcon = extension == ".xci" ? _xciIcon : _nspIcon;
                        }
                        catch (Exception exception)
                        {
                            Logger.Warning?.Print(LogClass.Application, $"The file encountered was not of a valid type. File: '{applicationPath}' Error: {exception}");
                        }
                    }
                    else if (extension == ".nro")
                    {
                        BinaryReader reader = new(file);

                        byte[] Read(long position, int size)
                        {
                            file.Seek(position, SeekOrigin.Begin);

                            return reader.ReadBytes(size);
                        }

                        try
                        {
                            file.Seek(24, SeekOrigin.Begin);

                            int assetOffset = reader.ReadInt32();

                            if (Encoding.ASCII.GetString(Read(assetOffset, 4)) == "ASET")
                            {
                                byte[] iconSectionInfo = Read(assetOffset + 8, 0x10);

                                long iconOffset = BitConverter.ToInt64(iconSectionInfo, 0);
                                long iconSize = BitConverter.ToInt64(iconSectionInfo, 8);

                                // Reads and stores game icon as byte array
                                if (iconSize > 0)
                                {
                                    applicationIcon = Read(assetOffset + iconOffset, (int)iconSize);
                                }
                                else
                                {
                                    applicationIcon = _nroIcon;
                                }
                            }
                            else
                            {
                                applicationIcon = _nroIcon;
                            }
                        }
                        catch
                        {
                            Logger.Warning?.Print(LogClass.Application, $"The file encountered was not of a valid type. Errored File: {applicationPath}");
                        }
                    }
                    else if (extension == ".nca")
                    {
                        applicationIcon = _ncaIcon;
                    }
                    // If its an NSO we just set defaults
                    else if (extension == ".nso")
                    {
                        applicationIcon = _nsoIcon;
                    }
                }
            }
            catch (Exception)
            {
                Logger.Warning?.Print(LogClass.Application, $"Could not retrieve a valid icon for the app. Default icon will be used. Errored File: {applicationPath}");
            }

            return applicationIcon ?? _ncaIcon;
        }

        private void GetApplicationInformation(ref ApplicationControlProperty controlData, ref ApplicationData data)
        {
            _ = Enum.TryParse(DesiredLanguage.ToString(), out TitleLanguage desiredTitleLanguage);

            if (controlData.Title.ItemsRo.Length > (int)desiredTitleLanguage)
            {
                data.Name = controlData.Title[(int)desiredTitleLanguage].NameString.ToString();
                data.Developer = controlData.Title[(int)desiredTitleLanguage].PublisherString.ToString();
            }
            else
            {
                data.Name = null;
                data.Developer = null;
            }

            if (string.IsNullOrWhiteSpace(data.Name))
            {
                foreach (ref readonly var controlTitle in controlData.Title.ItemsRo)
                {
                    if (!controlTitle.NameString.IsEmpty())
                    {
                        data.Name = controlTitle.NameString.ToString();

                        break;
                    }
                }
            }

            if (string.IsNullOrWhiteSpace(data.Developer))
            {
                foreach (ref readonly var controlTitle in controlData.Title.ItemsRo)
                {
                    if (!controlTitle.PublisherString.IsEmpty())
                    {
                        data.Developer = controlTitle.PublisherString.ToString();

                        break;
                    }
                }
            }

            if (data.Id == 0)
            {
                if (controlData.SaveDataOwnerId != 0)
                {
                    data.Id = controlData.SaveDataOwnerId;
                }
                else if (controlData.PresenceGroupId != 0)
                {
                    data.Id = controlData.PresenceGroupId;
                }
                else if (controlData.AddOnContentBaseId != 0)
                {
                    data.Id = (controlData.AddOnContentBaseId - 0x1000);
                }
            }

            data.Version = controlData.DisplayVersionString.ToString();
        }

        private bool IsUpdateApplied(Nca mainNca, out IFileSystem updatedControlFs)
        {
            updatedControlFs = null;

            string updatePath = null;

            try
            {
                (Nca patchNca, Nca controlNca) = mainNca.GetUpdateData(_virtualFileSystem, _checkLevel, 0, out updatePath);

                if (patchNca != null && controlNca != null)
                {
                    updatedControlFs = controlNca.OpenFileSystem(NcaSectionType.Data, IntegrityCheckLevel.None);

                    return true;
                }
            }
            catch (InvalidDataException)
            {
                Logger.Warning?.Print(LogClass.Application, $"The header key is incorrect or missing and therefore the NCA header content type check has failed. Errored File: {updatePath}");
            }
            catch (MissingKeyException exception)
            {
                Logger.Warning?.Print(LogClass.Application, $"Your key set is missing a key with the name: {exception.Name}. Errored File: {updatePath}");
            }

            return false;
        }

        private Nca TryOpenNca(IStorage ncaStorage)
        {
            try
            {
                return new Nca(_virtualFileSystem.KeySet, ncaStorage);
            }
            catch (Exception) { }

            return null;
        }
    }
}
