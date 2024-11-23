using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using BepInEx;
using BepInEx.Bootstrap;
using HarmonyLib;
using Jotunn.Extensions;
using Jotunn.Managers;
using UnityEngine;
using UnityEngine.UI;
using Object = UnityEngine.Object;

namespace Jotunn.Utils
{
    /// <summary>
    ///     Implementation of the mod compatibility features.
    /// </summary>
    public static class ModCompatibility
    {
        /// <summary>
        ///     Stores the last server message.
        /// </summary>
        private static ServerVersionData LastServerVersionData = new ServerVersionData();

        private static readonly Dictionary<string, ZPackage> ClientVersions = new Dictionary<string, ZPackage>();

        internal static void Init()
        {
            Main.LogInit("ModCompatibility");
            Main.Harmony.PatchAll(typeof(ModCompatibility));
        }

        /// <summary>
        ///     Check if a mod is installed and loaded on the server.<br/>
        ///     Can be called from both client and server.
        /// </summary>
        /// <param name="plugin">BepInEx mod to check</param>
        /// <returns>true if the mod is loaded on the server.<br/>false if the mod is not loaded on the server or no server connection is established</returns>
        public static bool IsModuleOnServer(BaseUnityPlugin plugin)
        {
            return IsModuleOnServer(plugin.Info.Metadata.GUID);
        }

        /// <summary>
        ///     Check if a mod is installed and loaded on the server.<br/>
        ///     Can be called from both client and server.
        /// </summary>
        /// <param name="modGUID">BepInEx mod GUID to check</param>
        /// <returns>true if the mod is loaded on the server.<br/>false if the mod is not loaded on the server or no server connection is established</returns>
        public static bool IsModuleOnServer(string modGUID)
        {
            if (ZNet.instance)
            {
                if (ZNet.instance.IsClientInstance())
                {
                    return LastServerVersionData.IsValid() && LastServerVersionData.moduleGUIDs.Contains(modGUID);
                }

                if (ZNet.instance.IsServer())
                {
                    return Chainloader.PluginInfos.ContainsKey(modGUID);
                }
            }

            return false;
        }

        /// <summary>
        ///     Check if Jotunn is installed and loaded on the server.<br/>
        ///     Can be called from both client and server.
        /// </summary>
        /// <returns>true if Jotunn is loaded on the server.<br/>false if Jotunn is not loaded on the server or no server connection is established</returns>
        public static bool IsJotunnOnServer()
        {
            return IsModuleOnServer(Main.ModGuid);
        }

        [HarmonyPatch(typeof(ZNet), nameof(ZNet.OnNewConnection)), HarmonyPrefix, HarmonyPriority(Priority.First)]
        private static void ZNet_OnNewConnection(ZNet __instance, ZNetPeer peer)
        {
            // clear the previous connection, if existing
            LastServerVersionData.Reset();

            // Register our RPC very early
            peer.m_rpc.Register<ZPackage>(nameof(RPC_Jotunn_ReceiveVersionData), RPC_Jotunn_ReceiveVersionData);
        }

        // Send client module list to server
        [HarmonyPatch(typeof(ZNet), nameof(ZNet.RPC_ClientHandshake)), HarmonyPrefix, HarmonyPriority(Priority.First)]
        private static void ZNet_RPC_ClientHandshake(ZNet __instance, ZRpc rpc)
        {
            rpc.Invoke(nameof(RPC_Jotunn_ReceiveVersionData), new ModuleVersionData(GetEnforcableMods().ToList()).ToZPackage());
        }

        // Send server module list to client
        [HarmonyPatch(typeof(ZNet), nameof(ZNet.RPC_ServerHandshake)), HarmonyPrefix, HarmonyPriority(Priority.First)]
        private static void ZNet_RPC_ServerHandshake(ZNet __instance, ZRpc rpc)
        {
            rpc.Invoke(nameof(RPC_Jotunn_ReceiveVersionData), new ModuleVersionData(GetEnforcableMods().ToList()).ToZPackage());
        }

        // Show mod compatibility error message when needed
        [HarmonyPatch(typeof(FejdStartup), nameof(FejdStartup.ShowConnectError)), HarmonyPostfix, HarmonyPriority(Priority.Last)]
        private static void FejdStartup_ShowConnectError(FejdStartup __instance)
        {
            if (LastServerVersionData.IsValid() && ZNet.m_connectionStatus == ZNet.ConnectionStatus.ErrorVersion)
            {
                string failedConnectionText = __instance.m_connectionFailedError.text;
                __instance.StartCoroutine(ShowModCompatibilityErrorMessage(failedConnectionText));
                __instance.m_connectionFailedPanel.SetActive(false);
            }
        }

        // Hook client sending of PeerInfo
        [HarmonyPatch(typeof(ZNet), nameof(ZNet.SendPeerInfo)), HarmonyPrefix, HarmonyPriority(Priority.First)]
        private static bool ZNet_SendPeerInfo(ZNet __instance, ZRpc rpc, string password)
        {
            if (ZNet.instance.IsClientInstance())
            {
                // If there was no server version response, Jötunn is not installed. Cancel if we have mandatory mods
                if (!LastServerVersionData.IsValid() && GetEnforcableMods().Any(x => x.IsNeededOnServer()))
                {
                    string missingMods = string.Join(Environment.NewLine, GetEnforcableMods().Where(x => x.IsNeededOnServer()).Select(x => x.ModName));
                    Logger.LogWarning("Jötunn is not installed on the server. Client has mandatory mods, cancelling connection. " +
                                      "Mods that need to be installed on the server:" + Environment.NewLine + missingMods);
                    rpc.Invoke("Disconnect");
                    LastServerVersionData = new ServerVersionData(new List<ModModule>());
                    ZNet.m_connectionStatus = ZNet.ConnectionStatus.ErrorVersion;
                    return false;
                }
            }

            return true;
        }

        // Hook RPC_PeerInfo to check in front of the original method
        [HarmonyPatch(typeof(ZNet), nameof(ZNet.RPC_PeerInfo)), HarmonyPrefix, HarmonyPriority(Priority.First)]
        private static bool ZNet_RPC_PeerInfo(ZNet __instance, ZRpc rpc, ZPackage pkg)
        {
            if (!ZNet.instance.IsClientInstance())
            {
                // Vanilla client trying to connect?
                if (!ClientVersions.ContainsKey(rpc.GetSocket().GetEndPointString()))
                {
                    // Check mods, if there are some installed on the server which need also to be on the client
                    if (GetEnforcableMods().Any(x => x.IsNeededOnClient()))
                    {
                        // There is a mod, which needs to be client side too
                        // Lets disconnect the vanilla client with Incompatible Version message

                        string missingMods = string.Join(Environment.NewLine, GetEnforcableMods().Where(x => x.IsNeededOnClient()).Select(x => x.ModName));
                        Logger.LogWarning("Jötunn is not installed on the client. Server has mandatory mods, cancelling connection. " +
                                          "Mods that need to be installed on the client:" + Environment.NewLine + missingMods);
                        rpc.Invoke("Error", (int)ZNet.ConnectionStatus.ErrorVersion);
                        return false;
                    }
                }
                else
                {
                    var serverData = new ModuleVersionData(GetEnforcableMods().ToList());
                    var clientData = new ModuleVersionData(ClientVersions[rpc.m_socket.GetEndPointString()]);

                    if (!CompareVersionData(serverData, clientData))
                    {
                        // Disconnect if mods are not network compatible
                        Logger.LogWarning("RPC_PeerInfo: Disconnecting modded client with incompatible version message. " +
                                          "Mods are not compatible");
                        rpc.Invoke("Error", (int)ZNet.ConnectionStatus.ErrorVersion);
                        return false;
                    }
                }
            }

            return true;
        }

        /// <summary>
        ///     Store server's message.
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="data"></param>
        private static void RPC_Jotunn_ReceiveVersionData(ZRpc sender, ZPackage data)
        {
            Logger.LogDebug($"Received Version package from {sender.m_socket.GetEndPointString()}");

            if (!ZNet.instance.IsClientInstance())
            {
                ClientVersions[sender.m_socket.GetEndPointString()] = data;
                var serverData = new ModuleVersionData(GetEnforcableMods().ToList());
                var clientData = new ModuleVersionData(data);

                if (!CompareVersionData(serverData, clientData))
                {
                    // Disconnect if mods are not network compatible
                    Logger.LogWarning("RPC_Jotunn_ReceiveVersionData: Disconnecting modded client with incompatible version message. " +
                                      "Mods are not compatible");
                    sender.Invoke("Error", (int)ZNet.ConnectionStatus.ErrorVersion);
                }
            }
            else
            {
                LastServerVersionData = new ServerVersionData(data);
            }
        }
        
        /// <summary>
        ///     Compares version data on server and client.
        /// </summary>
        /// <param name="serverData"></param>
        /// <param name="clientData"></param>
        /// <returns></returns>
        internal static bool CompareVersionData(ModuleVersionData serverData, ModuleVersionData clientData)
        {
            if (ReferenceEquals(serverData, clientData))
            {
                return true;
            }

            // Check for supported ModModule data layout
            if (!clientData.IsSupportedDataLayout)
            {
                Logger.LogWarning($"Jotunn version on client is higher than server version: {Main.Version}");
                return false;
            }

            if (!serverData.IsSupportedDataLayout)
            {
                Logger.LogWarning($"Jotunn version on server is higher than client version: {Main.Version}");
                return false;
            }

            // Check for compatible ModModule data layout versions
            if (serverData.ModModuleDataLayout != clientData.ModModuleDataLayout)
            {
                Logger.LogWarning("Jotunn versions on server and client are not compatible.");
                return false;
            }

            bool result = true;

            // Check server enforced mods
            foreach (var serverModule in FindNotInstalledMods(serverData, clientData))
            {
                Logger.LogWarning($"Missing mod on client: {serverModule.ModName}");
                result = false;
            }

            // Check client enforced mods
            foreach (var clientModule in FindAdditionalMods(serverData, clientData))
            {
                Logger.LogWarning($"Client loaded additional mod: {clientModule.ModName}");
                result = false;
            }

            // Check versions
            foreach (var serverModule in FindLowerVersionMods(serverData, clientData).Union(FindHigherVersionMods(serverData, clientData)))
            {
                var clientModule = clientData.FindModule(serverModule.ModID);
                Logger.LogWarning($"Mod version mismatch {serverModule.ModName}: Server {serverModule.Version}, Client {clientModule.Version}");
                result = false;
            }

            return result;
        }

        private static CompatibilityWindow LoadCompatWindow()
        {
            var localization = LocalizationManager.Instance.JotunnLocalization;
            localization.AddJsonFile("English", AssetUtils.LoadTextFromResources("English.json", typeof(Main).Assembly));
            localization.AddJsonFile("German", AssetUtils.LoadTextFromResources("German.json", typeof(Main).Assembly));

            AssetBundle bundle = AssetUtils.LoadAssetBundleFromResources("modcompat", typeof(Main).Assembly);
            var compatWindowGameObject = Object.Instantiate(bundle.LoadAsset<GameObject>("CompatibilityWindow"), GUIManager.CustomGUIFront.transform);
            bundle.Unload(false);

            var compatWindow = compatWindowGameObject.GetComponent<CompatibilityWindow>();
            var compatWindowRect = ((RectTransform)compatWindow.transform);

            foreach (var text in compatWindow.GetComponentsInChildren<Text>())
            {
                GUIManager.Instance.ApplyTextStyle(text, 18);
                text.text = Localization.instance.Localize(text.text);
            }

            GUIManager.Instance.ApplyWoodpanelStyle(compatWindow.transform);
            GUIManager.Instance.ApplyScrollRectStyle(compatWindow.scrollRect);
            GUIManager.Instance.ApplyButtonStyle(compatWindow.continueButton);
            GUIManager.Instance.ApplyButtonStyle(compatWindow.logFileButton);
            GUIManager.Instance.ApplyButtonStyle(compatWindow.troubleshootingButton);

            compatWindowRect.anchoredPosition = new Vector2(25, 0);
            compatWindow.gameObject.SetWidth(1000);
            compatWindow.gameObject.SetHeight(600);

            return compatWindow;
        }

        /// <summary>
        ///     Create and show mod compatibility error message
        /// </summary>
        private static IEnumerator ShowModCompatibilityErrorMessage(string failedConnectionText)
        {
            var compatWindow = LoadCompatWindow();
            var remote = LastServerVersionData.moduleVersionData;
            var local = new ModuleVersionData(GetEnforcableMods().ToList());

            // print issues to console
            CompareVersionData(remote, local);

            compatWindow.failedConnection.text = ColoredLine(GUIManager.Instance.ValheimOrange, "$mod_compat_header_failed_connection") +
                                                 failedConnectionText.Trim();
            compatWindow.localVersion.text = ColoredLine(GUIManager.Instance.ValheimOrange, "$mod_compat_header_local_version") +
                                             local.ToString(false).Trim();
            compatWindow.remoteVersion.text = ColoredLine(GUIManager.Instance.ValheimOrange, "$mod_compat_header_remote_version") +
                                              remote.ToString(false).Trim();
            compatWindow.errorMessages.text = CreateErrorMessage(remote, local).Trim();

            // Unity needs a frame to correctly calculate the preferred height. The components need to be active so we scale them down to 0
            // Their LayoutRebuilder.ForceRebuildLayoutImmediate does not take wrapped text into account
            compatWindow.transform.localScale = Vector3.zero;
            yield return null;
            compatWindow.transform.localScale = Vector3.one;

            compatWindow.UpdateTextPositions();
            compatWindow.continueButton.onClick.AddListener(() => Object.Destroy(compatWindow.gameObject));
            compatWindow.logFileButton.onClick.AddListener(OpenLogFile);
            compatWindow.troubleshootingButton.onClick.AddListener(OpenTroubleshootingPage);
            compatWindow.scrollRect.verticalNormalizedPosition = 1f;

            // Reset the last server version
            LastServerVersionData.Reset();
        }

        private static void OpenLogFile()
        {
            Application.OpenURL(BepInEx.Paths.BepInExRootPath);
        }

        private static void OpenTroubleshootingPage()
        {
            Application.OpenURL("https://github.com/Valheim-Modding/Wiki/wiki/Server-Troubleshooting");
        }

        /// <summary>
        ///     Create the error message(s) from the server and client message data
        /// </summary>
        /// <param name="serverData">server data</param>
        /// <param name="clientData">client data</param>
        /// <returns></returns>
        private static string CreateErrorMessage(ModuleVersionData serverData, ModuleVersionData clientData)
        {
            string dataLayoutErrMsg = CreateModModuleLayoutErrorMessage(serverData, clientData);
            if (!string.IsNullOrEmpty(dataLayoutErrMsg))
            {
                return CreateVanillaVersionErrorMessage(serverData, clientData) +
                       dataLayoutErrMsg +
                       CreateFurtherStepsMessage();
            }

            return CreateVanillaVersionErrorMessage(serverData, clientData) +
                   CreateNotInstalledErrorMessage(serverData, clientData) +
                   CreateLowerVersionErrorMessage(serverData, clientData) +
                   CreateHigherVersionErrorMessage(serverData, clientData) +
                   CreateAdditionalModsErrorMessage(serverData, clientData) +
                   CreateFurtherStepsMessage();
        }

        private static string CreateModModuleLayoutErrorMessage(ModuleVersionData serverData, ModuleVersionData clientData)
        {

            if (!clientData.IsSupportedDataLayout)
            {
                return ColoredLine(Color.red, $"Jotunn version on client is higher than server version: {Main.Version}");
            }

            if (!serverData.IsSupportedDataLayout)
            {
                return ColoredLine(Color.red, $"Jotunn version on server is higher than client version: {Main.Version}");
            }

            if (serverData.ModModuleDataLayout != clientData.ModModuleDataLayout)
            {
                return ColoredLine(Color.red, "Jotunn versions on server and client are not compatible.");
            }

            return string.Empty;
        }


        private static string CreateVanillaVersionErrorMessage(ModuleVersionData serverData, ModuleVersionData clientData)
        {
            if (serverData.NetworkVersion <= 0 || clientData.NetworkVersion <= 0)
            {
                return string.Empty;
            }

            if (serverData.NetworkVersion > clientData.NetworkVersion)
            {
                return ColoredLine(Color.red, "$mod_compat_header_valheim_version") +
                       ColoredLine(GUIManager.Instance.ValheimOrange, "$mod_compat_valheim_version_error_description", $"{serverData.NetworkVersion}", $"{clientData.NetworkVersion}") +
                       ColoredLine(Color.white, "$mod_compat_valheim_version_upgrade") +
                       Environment.NewLine;
            }

            if (serverData.NetworkVersion < clientData.NetworkVersion)
            {
                return ColoredLine(Color.red, "$mod_compat_header_valheim_version") +
                       ColoredLine(GUIManager.Instance.ValheimOrange, "$mod_compat_valheim_version_error_description", $"{serverData.NetworkVersion}", $"{clientData.NetworkVersion}") +
                       ColoredLine(Color.white, "$mod_compat_valheim_version_downgrade") +
                       Environment.NewLine;
            }

            return string.Empty;
        }

        private static string CreateNotInstalledErrorMessage(ModuleVersionData serverData, ModuleVersionData clientData)
        {
            List<ModModule> matchingServerMods = FindNotInstalledMods(serverData, clientData);

            if (matchingServerMods.Count == 0)
            {
                return string.Empty;
            }

            return ColoredLine(Color.red, "$mod_compat_header_missing_mods") +
                   ColoredLine(GUIManager.Instance.ValheimOrange, $"$mod_compat_missing_mods_description") +
                   string.Join("", matchingServerMods.Select(serverModule => ColoredLine(Color.white, "$mod_compat_missing_mod", $"{serverModule.ModName}", $"{serverModule.Version}"))) +
                   Environment.NewLine;
        }

        private static string CreateLowerVersionErrorMessage(ModuleVersionData serverData, ModuleVersionData clientData)
        {
            List<ModModule> matchingServerMods = FindLowerVersionMods(serverData, clientData);

            if (matchingServerMods.Count == 0)
            {
                return string.Empty;
            }

            return ColoredLine(Color.red, "$mod_compat_header_update_needed") +
                   string.Join("", matchingServerMods.Select((serverModule) => ColoredLine(Color.white, "$mod_compat_mod_update", serverModule.ModName, serverModule.GetVersionString()))) +
                   Environment.NewLine;
        }

        private static string CreateHigherVersionErrorMessage(ModuleVersionData serverData, ModuleVersionData clientData)
        {
            List<ModModule> matchingServerMods = FindHigherVersionMods(serverData, clientData);

            if (matchingServerMods.Count == 0)
            {
                return string.Empty;
            }

            return ColoredLine(Color.red, "$mod_compat_header_downgrade_needed") +
                   string.Join("", matchingServerMods.Select(serverModule => ColoredLine(Color.white, "$mod_compat_mod_downgrade", serverModule.ModName, serverModule.GetVersionString()))) +
                   Environment.NewLine;
        }

        private static string CreateAdditionalModsErrorMessage(ModuleVersionData serverData, ModuleVersionData clientData)
        {
            List<ModModule> matchingClientMods = FindAdditionalMods(serverData, clientData);

            if (matchingClientMods.Count == 0)
            {
                return string.Empty;
            }

            return ColoredLine(Color.red, "$mod_compat_header_additional_mods") +
                   ColoredLine(GUIManager.Instance.ValheimOrange, "$mod_compat_additional_mods_description") +
                   string.Join("", matchingClientMods.Select(clientModule => ColoredLine(Color.white, "$mod_compat_additional_mod", clientModule.ModName, $"{clientModule.Version}"))) +
                   Environment.NewLine;
        }

        private static string CreateFurtherStepsMessage()
        {
            return ColoredLine(GUIManager.Instance.ValheimOrange, "$mod_compat_header_further_steps") +
                   ColoredLine(Color.white, "$mod_compat_further_steps_description") +
                   Environment.NewLine;
        }

        private static List<ModModule> FindNotInstalledMods(ModuleVersionData serverData, ModuleVersionData clientData)
        {
            return FindMods(serverData, clientData, (serverModule, clientModule) =>
            {
                return serverModule.IsNeededOnClient() && clientModule == null;
            }).ToList();
        }

        private static List<ModModule> FindAdditionalMods(ModuleVersionData serverData, ModuleVersionData clientData)
        {
            return FindMods(clientData, serverData, (clientModule, serverModule) =>
            {
                return clientModule.IsNeededOnServer() && serverModule == null;
            }).ToList();
        }

        private static List<ModModule> FindLowerVersionMods(ModuleVersionData serverData, ModuleVersionData clientData)
        {
            return FindMods(serverData, clientData, (serverModule, clientModule) =>
            {
                return clientModule != null && ModModule.IsLowerVersion(serverModule, clientModule, serverModule.VersionStrictness);
            }).ToList();
        }

        private static List<ModModule> FindHigherVersionMods(ModuleVersionData serverData, ModuleVersionData clientData)
        {
            return FindMods(serverData, clientData, (serverModule, clientModule) =>
            {
                return clientModule != null && ModModule.IsLowerVersion(clientModule, serverModule, serverModule.VersionStrictness);
            }).ToList();
        }

        private static IEnumerable<ModModule> FindMods(ModuleVersionData baseModules, ModuleVersionData additionalModules, Func<ModModule, ModModule, bool> predicate)
        {
            foreach (ModModule baseModule in baseModules.Modules)
            {
                ModModule additionalModule = additionalModules.FindModule(baseModule.ModID);

                if (predicate(baseModule, additionalModule))
                {
                    yield return baseModule;
                }
            }
        }

        /// <summary>
        ///     Get module.
        /// </summary>
        /// <returns></returns>
        internal static IEnumerable<ModModule> GetEnforcableMods()
        {
            foreach (var plugin in BepInExUtils.GetDependentPlugins(true).OrderBy(x => x.Key))
            {
                var networkCompatibilityAttribute = plugin.Value.GetNetworkCompatibilityAttribute();

                if (networkCompatibilityAttribute != null)
                {
                    yield return new ModModule(plugin.Value.Info.Metadata, networkCompatibilityAttribute);
                }
                else
                {
                    yield return new ModModule(plugin.Value.Info.Metadata);
                }
            }
        }

        private static string ColoredLine(Color color, string inner, params string[] words)
        {
            return $"<color=#{ColorUtility.ToHtmlStringRGB(color)}>{Localization.instance.Localize(inner, words)}</color>{Environment.NewLine}";
        }
    }
}
