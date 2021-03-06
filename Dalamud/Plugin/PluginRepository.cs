using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Serilog;

namespace Dalamud.Plugin
{
    internal class PluginRepository {
        private string PluginFunctionBaseUrl => "https://us-central1-xl-functions.cloudfunctions.net/download-plugin/?plugin={0}&isUpdate={1}&isTesting={2}";
        private string PluginMasterUrl => "https://raw.githubusercontent.com/goatcorp/DalamudPlugins/master/pluginmaster.json";
        private string PluginJsonUrl =
            "https://raw.githubusercontent.com/goatcorp/DalamudPlugins/master/{0}/{1}/{1}.json";


        private readonly Dalamud dalamud;
        private string pluginDirectory;
        public ReadOnlyCollection<PluginDefinition> PluginMaster;

        public enum InitializationState {
            Unknown,
            InProgress,
            Success,
            Fail
        }

        public InitializationState State { get; private set; }

        public PluginRepository(Dalamud dalamud, string pluginDirectory, string gameVersion) {
            this.dalamud = dalamud;
            this.pluginDirectory = pluginDirectory;

            ReloadPluginMasterAsync();
        }

        public void ReloadPluginMasterAsync() {
            Task.Run(() => {
                this.PluginMaster = null;

                State = InitializationState.InProgress;

                try {
                    using var client = new WebClient();

                    var data = client.DownloadString(PluginMasterUrl);

                    var unsortedPluginMaster = JsonConvert.DeserializeObject<List<PluginDefinition>>(data);
                    unsortedPluginMaster.Sort((a, b) => a.Name.CompareTo(b.Name));
                    this.PluginMaster = unsortedPluginMaster.AsReadOnly();

                    State = InitializationState.Success;
                }
                catch (Exception ex) {
                    Log.Error(ex, "Could not download PluginMaster");
                    State = InitializationState.Fail;
                }
            }).ContinueWith(t => {
                if (t.IsFaulted)
                    State = InitializationState.Fail;
            });
        }

        public bool InstallPlugin(PluginDefinition definition, bool enableAfterInstall = true, bool isUpdate = false, bool fromTesting = false) {
            try {
                using var client = new WebClient();

                // We need to redownload the json, for the eventuality of the zip having changed after PM download
                definition = JsonConvert.DeserializeObject<PluginDefinition>(
                    client.DownloadString(string.Format(this.PluginJsonUrl, fromTesting ? "testing" : "plugins",
                                                        definition.InternalName)));

                var outputDir = new DirectoryInfo(Path.Combine(this.pluginDirectory, definition.InternalName, fromTesting ? definition.TestingAssemblyVersion : definition.AssemblyVersion));
                var dllFile = new FileInfo(Path.Combine(outputDir.FullName, $"{definition.InternalName}.dll"));
                var disabledFile = new FileInfo(Path.Combine(outputDir.FullName, ".disabled"));
                var testingFile = new FileInfo(Path.Combine(outputDir.FullName, ".testing"));
                var wasDisabled = disabledFile.Exists;

                if (dllFile.Exists && enableAfterInstall) {
                    if (disabledFile.Exists)
                        disabledFile.Delete();

                    return this.dalamud.PluginManager.LoadPluginFromAssembly(dllFile, false, PluginLoadReason.Installer);
                }

                if (dllFile.Exists && !enableAfterInstall) {
                    return true;
                }

                try {
                    if (outputDir.Exists)
                        outputDir.Delete(true);
                    outputDir.Create();
                } catch {
                    // ignored, since the plugin may be loaded already
                }

                var path = Path.GetTempFileName();

                var doTestingDownload = false;
                if ((Version.TryParse(definition.TestingAssemblyVersion, out var testingAssemblyVer) || definition.IsTestingExclusive)
                    && fromTesting) {
                    doTestingDownload = testingAssemblyVer > Version.Parse(definition.AssemblyVersion) || definition.IsTestingExclusive;
                }
                
                var url = string.Format(PluginFunctionBaseUrl, definition.InternalName, isUpdate, doTestingDownload);

                Log.Information("Downloading plugin to {0} from {1} doTestingDownload:{2} isTestingExclusive:{3}", path, url, doTestingDownload, definition.IsTestingExclusive);

                client.DownloadFile(url, path);

                Log.Information("Extracting to {0}", outputDir);

                ZipFile.ExtractToDirectory(path, outputDir.FullName);

                if (wasDisabled || !enableAfterInstall) {
                    disabledFile.Create();
                    return true;
                }

                if (doTestingDownload) {
                    testingFile.Create();
                } else {
                    if (testingFile.Exists)
                        testingFile.Delete();
                }

                return this.dalamud.PluginManager.LoadPluginFromAssembly(dllFile, false, PluginLoadReason.Installer);
            }
            catch (Exception e) {
                Log.Error(e, "Plugin download failed hard.");
                return false;
            }
        }

        internal class PluginUpdateStatus {
            public string InternalName { get; set; }
            public string Name { get; set; }
            public string Version { get; set; }
            public bool WasUpdated { get; set; }
        }

        public (bool Success, List<PluginUpdateStatus> UpdatedPlugins) UpdatePlugins(bool dryRun = false) {
            Log.Information("Starting plugin update... dry:{0}", dryRun);

            var updatedList = new List<PluginUpdateStatus>();
            var hasError = false;

            try {
                var pluginsDirectory = new DirectoryInfo(this.pluginDirectory);
                foreach (var installed in pluginsDirectory.GetDirectories()) {
                    try {
                        var versions = installed.GetDirectories();

                        if (versions.Length == 0) {
                            Log.Information("Has no versions: {0}", installed.FullName);
                            continue;
                        }

                        var sortedVersions = versions.OrderBy(dirInfo => {
                            var success = Version.TryParse(dirInfo.Name, out Version version);
                            if (!success) { Log.Debug("Unparseable version: {0}", dirInfo.Name); }
                            return version;
                        });
                        var latest = sortedVersions.Last();

                        var localInfoFile = new FileInfo(Path.Combine(latest.FullName, $"{installed.Name}.json"));

                        if (!localInfoFile.Exists) {
                            Log.Information("Has no definition: {0}", localInfoFile.FullName);
                            continue;
                        }

                        var info = JsonConvert.DeserializeObject<PluginDefinition>(
                            File.ReadAllText(localInfoFile.FullName));

                        var remoteInfo = this.PluginMaster.FirstOrDefault(x => x.Name == info.Name);

                        if (remoteInfo == null) {
                            Log.Information("Is not in pluginmaster: {0}", info.Name);
                            continue;
                        }

                        if (remoteInfo.DalamudApiLevel < PluginManager.DALAMUD_API_LEVEL) {
                            Log.Information("Has not applicable API level: {0}", info.Name);
                            continue;
                        }

                        Version.TryParse(remoteInfo.AssemblyVersion, out Version remoteAssemblyVer);
                        Version.TryParse(info.AssemblyVersion, out Version localAssemblyVer);

                        var testingAvailable = false;
                        if (!string.IsNullOrEmpty(remoteInfo.TestingAssemblyVersion)) {
                            Version.TryParse(remoteInfo.TestingAssemblyVersion, out var testingAssemblyVer);
                            testingAvailable = testingAssemblyVer > localAssemblyVer && this.dalamud.Configuration.DoPluginTest;
                        }
                        
                        if (remoteAssemblyVer > localAssemblyVer || testingAvailable) {
                            Log.Information("Eligible for update: {0}", remoteInfo.InternalName);

                            // DisablePlugin() below immediately creates a .disabled file anyway, but will fail
                            // with an exception if we try to do it twice in row like this

                            if (!dryRun) {
                                var wasEnabled =
                                    this.dalamud.PluginManager.Plugins.Where(x => x.Definition != null).Any(
                                        x => x.Definition.InternalName == info.InternalName);
                                ;

                                Log.Verbose("wasEnabled: {0}", wasEnabled);

                                // Try to disable plugin if it is loaded
                                if (wasEnabled) {
                                    try {
                                        this.dalamud.PluginManager.DisablePlugin(info);
                                    }
                                    catch (Exception ex) {
                                        Log.Error(ex, "Plugin disable failed");
                                        //hasError = true;
                                    }
                                }

                                try {
                                    // Just to be safe
                                    foreach (var sortedVersion in sortedVersions) {
                                        var disabledFile =
                                            new FileInfo(Path.Combine(sortedVersion.FullName, ".disabled"));
                                        if (!disabledFile.Exists)
                                            disabledFile.Create();
                                    }
                                } catch (Exception ex) {
                                    Log.Error(ex, "Plugin disable old versions failed");
                                }

                                var installSuccess = InstallPlugin(remoteInfo, wasEnabled, true, testingAvailable);

                                if (!installSuccess) {
                                    Log.Error("InstallPlugin failed.");
                                    hasError = true;
                                }

                                updatedList.Add(new PluginUpdateStatus {
                                    InternalName = remoteInfo.InternalName,
                                    Name = remoteInfo.Name,
                                    Version = testingAvailable ? remoteInfo.TestingAssemblyVersion : remoteInfo.AssemblyVersion,
                                    WasUpdated = installSuccess
                                });
                            } else {
                                updatedList.Add(new PluginUpdateStatus {
                                    InternalName = remoteInfo.InternalName,
                                    Name = remoteInfo.Name,
                                    Version = testingAvailable ? remoteInfo.TestingAssemblyVersion : remoteInfo.AssemblyVersion,
                                    WasUpdated = true
                                });
                            }
                        } else {
                            Log.Information("Up to date: {0}", remoteInfo.InternalName);
                        }
                    } catch (Exception ex) {
                        Log.Error(ex, "Could not update plugin: {0}", installed.FullName);
                    }
                }
            }
            catch (Exception e) {
                Log.Error(e, "Plugin update failed.");
                hasError = true;
            }

            Log.Information("Plugin update OK.");

            return (!hasError, updatedList);
        }

        public void CleanupPlugins() {
            try {
                var pluginsDirectory = new DirectoryInfo(this.pluginDirectory);
                foreach (var installed in pluginsDirectory.GetDirectories()) {
                    var versions = installed.GetDirectories();

                    if (versions.Length == 0) {
                        Log.Information("[PLUGINR] Has no versions: {0}", installed.FullName);
                        continue;
                    }

                    var sortedVersions = versions.OrderBy(dirInfo => {
                        var success = Version.TryParse(dirInfo.Name, out Version version);
                        if (!success) { Log.Debug("Unparseable version: {0}", dirInfo.Name); }
                        return version;
                    }).ToArray();
                    for (var i = 0; i < sortedVersions.Length - 1; i++) {
                        var disabledFile = new FileInfo(Path.Combine(sortedVersions[i].FullName, ".disabled"));
                        if (disabledFile.Exists) {
                            Log.Information("[PLUGINR] Trying to delete old {0} at {1}", installed.Name, sortedVersions[i].FullName);
                            try {
                                sortedVersions[i].Delete(true);
                            }
                            catch (Exception ex) {
                                Log.Error(ex, "[PLUGINR] Could not delete old version");
                            }
                        }
                    }
                }
            }
            catch (Exception ex) {
                Log.Error(ex, "[PLUGINR] Plugin cleanup failed.");
            }
        }
    }
}
