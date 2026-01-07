// Requires: ImageLibrary
using System;
using System.Collections.Generic;
using Oxide.Core;
using Oxide.Core.Plugins;
using Oxide.Game.Rust.Cui;
using UnityEngine;

namespace Oxide.Plugins
{
    [Info("WelcomeScreen", "HoodWars", "1.0.0")]
    [Description("GTA5-style welcome screen with ImageLibrary integration")]
    public class WelcomeScreen : RustPlugin
    {
        #region Fields

        [PluginReference] private Plugin ImageLibrary;

        private const string WelcomeUIName = "WelcomeScreen_Main";
        private const string InfoUIName = "WelcomeScreen_Info";
        private const string PermissionBypass = "welcomescreen.bypass";

        private HashSet<ulong> _hasSeenWelcome = new HashSet<ulong>();
        private Dictionary<ulong, Timer> _autoCloseTimers = new Dictionary<ulong, Timer>();

        #endregion

        #region Configuration

        private ConfigData _config;

        private class ConfigData
        {
            public string BackgroundImageUrl { get; set; }
            public string LogoImageUrl { get; set; }
            public string ServerName { get; set; }
            public string ServerTagline { get; set; }
            public string WelcomeMessage { get; set; }
            public float AutoCloseSeconds { get; set; }
            public bool ShowOnConnect { get; set; }
            public bool ShowOnRespawn { get; set; }
            public List<string> InfoLines { get; set; }
            public List<CommandInfo> ImportantCommands { get; set; }
        }

        private class CommandInfo
        {
            public string Command { get; set; }
            public string Description { get; set; }
        }

        protected override void LoadDefaultConfig()
        {
            _config = new ConfigData
            {
                // GTA5-style dark cityscape background - replace with your own URL
                BackgroundImageUrl = "https://i.imgur.com/cVpkuMJ.png",
                // Server logo - replace with your own
                LogoImageUrl = "",
                ServerName = "HOOD WARS",
                ServerTagline = "SURVIVE. CONQUER. DOMINATE.",
                WelcomeMessage = "Welcome to the streets. Choose your hood wisely.",
                AutoCloseSeconds = 0, // 0 = manual close only
                ShowOnConnect = true,
                ShowOnRespawn = false,
                InfoLines = new List<string>
                {
                    "<color=#FFD700>HOW TO PLAY</color>",
                    "",
                    "<color=#FF6B6B>1. CLAIM YOUR HOOD</color>",
                    "Place a Tool Cupboard (TC) in any gang territory to join that gang.",
                    "You'll receive your gang's colors and weapon automatically.",
                    "",
                    "<color=#4ECDC4>2. DEFEND YOUR TERRITORY</color>",
                    "Enemy gang members entering your territory will trigger drive-by attacks.",
                    "",
                    "",
                    "<color=#95E1D3>3. BUILD YOUR REP</color>",
                    "Take down rivals to earn reputation and climb the ranks.",
                    "The gang with the most territory wins.",
                    "",
                    "<color=#F38181>4. GANG TERRITORIES</color>",
                    "• <color=#FF0000>Westside Pirus</color> - Northwest",
                    "• <color=#FFD700>Northside Vagos</color> - Northeast", 
                    "• <color=#0000FF>Southside Sureños</color> - Southwest",
                    "• <color=#00FF00>Eastside Disciples</color> - Southeast"
                },
                ImportantCommands = new List<CommandInfo>
                {
                    new CommandInfo { Command = "/whoami", Description = "Check your gang membership and rep status" },
                    new CommandInfo { Command = "/gangname [name]", Description = "Set your sub-gang/crew name (max 15 chars)" },
                    new CommandInfo { Command = "/tag", Description = "Open gang tag menu to get spray cans" },
                    new CommandInfo { Command = "/tagstats", Description = "View your tag stats and territory heat levels" },
                    new CommandInfo { Command = "/covertag", Description = "Cover an enemy tag you're looking at" },
                    new CommandInfo { Command = "/claimdoor", Description = "Claim a Room and get your code" },
                    new CommandInfo { Command = "/welcome", Description = "Show this welcome screen again" },
                    new CommandInfo { Command = "/info", Description = "Show server info and commands" }
                }
            };
            SaveConfig();
        }

        protected override void LoadConfig()
        {
            base.LoadConfig();
            try
            {
                _config = Config.ReadObject<ConfigData>();
                if (_config == null)
                    LoadDefaultConfig();
            }
            catch
            {
                LoadDefaultConfig();
            }
        }

        protected override void SaveConfig() => Config.WriteObject(_config);

        #endregion

        #region Hooks

        private void Init()
        {
            permission.RegisterPermission(PermissionBypass, this);
        }

        private void OnServerInitialized()
        {
            if (ImageLibrary == null || !ImageLibrary.IsLoaded)
            {
                PrintWarning("ImageLibrary is not loaded! Images will not display.");
                return;
            }

            // Register images with ImageLibrary
            if (!string.IsNullOrEmpty(_config.BackgroundImageUrl))
            {
                ImageLibrary.Call("AddImage", _config.BackgroundImageUrl, "WelcomeScreen_Background");
                Puts($"[DEBUG] Registered background image: {_config.BackgroundImageUrl}");
            }

            if (!string.IsNullOrEmpty(_config.LogoImageUrl))
            {
                ImageLibrary.Call("AddImage", _config.LogoImageUrl, "WelcomeScreen_Logo");
                Puts($"[DEBUG] Registered logo image: {_config.LogoImageUrl}");
            }
        }

        private void Unload()
        {
            foreach (var player in BasePlayer.activePlayerList)
            {
                DestroyAllUI(player);
            }

            foreach (var timer in _autoCloseTimers.Values)
            {
                timer?.Destroy();
            }
            _autoCloseTimers.Clear();
        }

        private void OnPlayerConnected(BasePlayer player)
        {
            if (player == null) return;
            if (!_config.ShowOnConnect) return;
            if (permission.UserHasPermission(player.UserIDString, PermissionBypass)) return;

            // Delay to let player fully load in
            timer.Once(3f, () =>
            {
                if (player != null && player.IsConnected)
                {
                    ShowWelcomeScreen(player);
                }
            });
        }

        private void OnPlayerRespawned(BasePlayer player)
        {
            if (player == null) return;
            if (!_config.ShowOnRespawn) return;
            if (_hasSeenWelcome.Contains(player.userID)) return;
            if (permission.UserHasPermission(player.UserIDString, PermissionBypass)) return;

            ShowWelcomeScreen(player);
        }

        #endregion

        #region Commands

        [ChatCommand("welcome")]
        private void CmdWelcome(BasePlayer player, string command, string[] args)
        {
            ShowWelcomeScreen(player);
        }

        [ChatCommand("info")]
        private void CmdInfo(BasePlayer player, string command, string[] args)
        {
            ShowInfoScreen(player);
        }

        [ConsoleCommand("welcomescreen.close")]
        private void CmdCloseUI(ConsoleSystem.Arg arg)
        {
            var player = arg.Player();
            if (player == null) return;

            DestroyAllUI(player);
            _hasSeenWelcome.Add(player.userID);
        }

        [ConsoleCommand("welcomescreen.showinfo")]
        private void CmdShowInfoFromWelcome(ConsoleSystem.Arg arg)
        {
            var player = arg.Player();
            if (player == null) return;

            DestroyUI(player, WelcomeUIName);
            ShowInfoScreen(player);
        }

        [ConsoleCommand("welcomescreen.showwelcome")]
        private void CmdShowWelcomeFromInfo(ConsoleSystem.Arg arg)
        {
            var player = arg.Player();
            if (player == null) return;

            DestroyUI(player, InfoUIName);
            ShowWelcomeScreen(player);
        }

        [ConsoleCommand("welcomescreen.reload")]
        private void CmdReloadImages(ConsoleSystem.Arg arg)
        {
            if (arg.Player() != null && !arg.Player().IsAdmin) return;

            if (ImageLibrary == null || !ImageLibrary.IsLoaded)
            {
                Puts("ImageLibrary not loaded!");
                return;
            }

            // Re-register images
            if (!string.IsNullOrEmpty(_config.BackgroundImageUrl))
            {
                ImageLibrary.Call("AddImage", _config.BackgroundImageUrl, "WelcomeScreen_Background");
            }
            if (!string.IsNullOrEmpty(_config.LogoImageUrl))
            {
                ImageLibrary.Call("AddImage", _config.LogoImageUrl, "WelcomeScreen_Logo");
            }

            Puts("Welcome screen images reloaded!");
        }

        #endregion

        #region UI Methods

        private void ShowWelcomeScreen(BasePlayer player)
        {
            DestroyAllUI(player);

            var elements = new CuiElementContainer();

            // Full-screen dark overlay/background
            string backgroundImage = GetImageId("WelcomeScreen_Background");
            if (!string.IsNullOrEmpty(backgroundImage))
            {
                elements.Add(new CuiElement
                {
                    Name = WelcomeUIName,
                    Parent = "Overlay",
                    Components =
                    {
                        new CuiRawImageComponent { Png = backgroundImage, Color = "1 1 1 0.95" },
                        new CuiRectTransformComponent { AnchorMin = "0 0", AnchorMax = "1 1" }
                    }
                });
            }
            else
            {
                // Fallback to dark gradient background if no image
                elements.Add(new CuiPanel
                {
                    Image = { Color = "0.05 0.05 0.08 0.98" },
                    RectTransform = { AnchorMin = "0 0", AnchorMax = "1 1" },
                    CursorEnabled = true
                }, "Overlay", WelcomeUIName);
            }

            // GTA-style vertical lines effect (decorative)
            elements.Add(new CuiPanel
            {
                Image = { Color = "0 0 0 0.3" },
                RectTransform = { AnchorMin = "0 0", AnchorMax = "0.003 1" }
            }, WelcomeUIName);
            elements.Add(new CuiPanel
            {
                Image = { Color = "0 0 0 0.3" },
                RectTransform = { AnchorMin = "0.997 0", AnchorMax = "1 1" }
            }, WelcomeUIName);

            // Logo (if available)
            string logoImage = GetImageId("WelcomeScreen_Logo");
            if (!string.IsNullOrEmpty(logoImage))
            {
                elements.Add(new CuiElement
                {
                    Parent = WelcomeUIName,
                    Components =
                    {
                        new CuiRawImageComponent { Png = logoImage, Color = "1 1 1 1" },
                        new CuiRectTransformComponent { AnchorMin = "0.35 0.55", AnchorMax = "0.65 0.85" }
                    }
                });
            }

            // Server name - GTA style large text
            elements.Add(new CuiLabel
            {
                Text = { 
                    Text = _config.ServerName, 
                    FontSize = 52, 
                    Align = TextAnchor.MiddleCenter, 
                    Color = "1 0.85 0.2 1",
                    Font = "robotocondensed-bold.ttf"
                },
                RectTransform = { AnchorMin = "0 0.42", AnchorMax = "1 0.55" }
            }, WelcomeUIName);

            // Tagline
            elements.Add(new CuiLabel
            {
                Text = { 
                    Text = _config.ServerTagline, 
                    FontSize = 18, 
                    Align = TextAnchor.MiddleCenter, 
                    Color = "0.9 0.9 0.9 0.9",
                    Font = "robotocondensed-regular.ttf"
                },
                RectTransform = { AnchorMin = "0 0.36", AnchorMax = "1 0.42" }
            }, WelcomeUIName);

            // Welcome message
            elements.Add(new CuiLabel
            {
                Text = { 
                    Text = _config.WelcomeMessage, 
                    FontSize = 14, 
                    Align = TextAnchor.MiddleCenter, 
                    Color = "0.7 0.7 0.7 1",
                    Font = "robotocondensed-regular.ttf"
                },
                RectTransform = { AnchorMin = "0.2 0.28", AnchorMax = "0.8 0.34" }
            }, WelcomeUIName);

            // "Press ENTER to continue" or buttons
            // ENTER button - GTA style
            elements.Add(new CuiButton
            {
                Button = { Color = "0.8 0.6 0.1 0.9", Command = "welcomescreen.close" },
                RectTransform = { AnchorMin = "0.35 0.15", AnchorMax = "0.65 0.22" },
                Text = { 
                    Text = "▶  ENTER THE STREETS", 
                    FontSize = 16, 
                    Align = TextAnchor.MiddleCenter,
                    Font = "robotocondensed-bold.ttf"
                }
            }, WelcomeUIName);

            // Info button
            elements.Add(new CuiButton
            {
                Button = { Color = "0.2 0.2 0.25 0.9", Command = "welcomescreen.showinfo" },
                RectTransform = { AnchorMin = "0.35 0.07", AnchorMax = "0.65 0.13" },
                Text = { 
                    Text = "HOW TO PLAY / COMMANDS", 
                    FontSize = 12, 
                    Align = TextAnchor.MiddleCenter,
                    Font = "robotocondensed-regular.ttf",
                    Color = "0.8 0.8 0.8 1"
                }
            }, WelcomeUIName);

            // Bottom decorative line
            elements.Add(new CuiPanel
            {
                Image = { Color = "0.8 0.6 0.1 0.5" },
                RectTransform = { AnchorMin = "0.3 0.05", AnchorMax = "0.7 0.052" }
            }, WelcomeUIName);

            // Version/copyright text
            elements.Add(new CuiLabel
            {
                Text = { 
                    Text = "Hood Wars v1.0 | Type /welcome to see this screen again", 
                    FontSize = 10, 
                    Align = TextAnchor.MiddleCenter, 
                    Color = "0.4 0.4 0.4 1" 
                },
                RectTransform = { AnchorMin = "0 0.01", AnchorMax = "1 0.04" }
            }, WelcomeUIName);

            CuiHelper.AddUi(player, elements);

            // Auto-close timer if configured
            if (_config.AutoCloseSeconds > 0)
            {
                if (_autoCloseTimers.ContainsKey(player.userID))
                {
                    _autoCloseTimers[player.userID]?.Destroy();
                }

                _autoCloseTimers[player.userID] = timer.Once(_config.AutoCloseSeconds, () =>
                {
                    if (player != null && player.IsConnected)
                    {
                        DestroyAllUI(player);
                        _hasSeenWelcome.Add(player.userID);
                    }
                });
            }

            Puts($"[DEBUG] Showed welcome screen to {player.displayName}");
        }

        private void ShowInfoScreen(BasePlayer player)
        {
            DestroyAllUI(player);

            var elements = new CuiElementContainer();

            // Dark background with slight transparency
            elements.Add(new CuiPanel
            {
                Image = { Color = "0.08 0.08 0.1 0.98" },
                RectTransform = { AnchorMin = "0 0", AnchorMax = "1 1" },
                CursorEnabled = true
            }, "Overlay", InfoUIName);

            // Header bar
            elements.Add(new CuiPanel
            {
                Image = { Color = "0.8 0.6 0.1 0.9" },
                RectTransform = { AnchorMin = "0 0.92", AnchorMax = "1 1" }
            }, InfoUIName);

            // Header text
            elements.Add(new CuiLabel
            {
                Text = { 
                    Text = $"  {_config.ServerName} - INFORMATION", 
                    FontSize = 24, 
                    Align = TextAnchor.MiddleLeft, 
                    Color = "0.1 0.1 0.1 1",
                    Font = "robotocondensed-bold.ttf"
                },
                RectTransform = { AnchorMin = "0.02 0.92", AnchorMax = "0.7 1" }
            }, InfoUIName);

            // Close button in header
            elements.Add(new CuiButton
            {
                Button = { Color = "0.6 0.1 0.1 0.9", Command = "welcomescreen.close" },
                RectTransform = { AnchorMin = "0.92 0.935", AnchorMax = "0.99 0.985" },
                Text = { Text = "✕", FontSize = 20, Align = TextAnchor.MiddleCenter }
            }, InfoUIName);

            // Left panel - How to Play
            elements.Add(new CuiPanel
            {
                Image = { Color = "0.12 0.12 0.15 0.95" },
                RectTransform = { AnchorMin = "0.02 0.08", AnchorMax = "0.58 0.90" }
            }, InfoUIName, "InfoPanel_Left");

            // How to play content
            string infoText = string.Join("\n", _config.InfoLines);
            elements.Add(new CuiLabel
            {
                Text = { 
                    Text = infoText, 
                    FontSize = 13, 
                    Align = TextAnchor.UpperLeft, 
                    Color = "0.9 0.9 0.9 1",
                    Font = "robotocondensed-regular.ttf"
                },
                RectTransform = { AnchorMin = "0.03 0.02", AnchorMax = "0.97 0.98" }
            }, "InfoPanel_Left");

            // Right panel - Commands
            elements.Add(new CuiPanel
            {
                Image = { Color = "0.12 0.12 0.15 0.95" },
                RectTransform = { AnchorMin = "0.60 0.08", AnchorMax = "0.98 0.90" }
            }, InfoUIName, "InfoPanel_Right");

            // Commands header
            elements.Add(new CuiLabel
            {
                Text = { 
                    Text = "<color=#FFD700>IMPORTANT COMMANDS</color>", 
                    FontSize = 16, 
                    Align = TextAnchor.MiddleCenter, 
                    Color = "1 1 1 1",
                    Font = "robotocondensed-bold.ttf"
                },
                RectTransform = { AnchorMin = "0 0.90", AnchorMax = "1 0.98" }
            }, "InfoPanel_Right");

            // Command list
            float y = 0.85f;
            float rowHeight = 0.08f;
            foreach (var cmd in _config.ImportantCommands)
            {
                // Command box
                elements.Add(new CuiPanel
                {
                    Image = { Color = "0.18 0.18 0.22 0.9" },
                    RectTransform = { AnchorMin = $"0.03 {y - rowHeight}", AnchorMax = $"0.97 {y}" }
                }, "InfoPanel_Right", $"Cmd_{cmd.Command}");

                // Command text
                elements.Add(new CuiLabel
                {
                    Text = { 
                        Text = $"<color=#4ECDC4>{cmd.Command}</color>", 
                        FontSize = 14, 
                        Align = TextAnchor.MiddleLeft, 
                        Color = "1 1 1 1",
                        Font = "robotocondensed-bold.ttf"
                    },
                    RectTransform = { AnchorMin = "0.05 0", AnchorMax = "0.35 1" }
                }, $"Cmd_{cmd.Command}");

                // Description
                elements.Add(new CuiLabel
                {
                    Text = { 
                        Text = cmd.Description, 
                        FontSize = 11, 
                        Align = TextAnchor.MiddleLeft, 
                        Color = "0.7 0.7 0.7 1" 
                    },
                    RectTransform = { AnchorMin = "0.36 0", AnchorMax = "0.98 1" }
                }, $"Cmd_{cmd.Command}");

                y -= rowHeight + 0.015f;
            }

            // Back to welcome button
            elements.Add(new CuiButton
            {
                Button = { Color = "0.3 0.3 0.35 0.9", Command = "welcomescreen.showwelcome" },
                RectTransform = { AnchorMin = "0.02 0.02", AnchorMax = "0.20 0.06" },
                Text = { Text = "◀ BACK", FontSize = 12, Align = TextAnchor.MiddleCenter }
            }, InfoUIName);

            // Play button
            elements.Add(new CuiButton
            {
                Button = { Color = "0.8 0.6 0.1 0.9", Command = "welcomescreen.close" },
                RectTransform = { AnchorMin = "0.75 0.02", AnchorMax = "0.98 0.06" },
                Text = { 
                    Text = "PLAY ▶", 
                    FontSize = 14, 
                    Align = TextAnchor.MiddleCenter,
                    Font = "robotocondensed-bold.ttf"
                }
            }, InfoUIName);

            CuiHelper.AddUi(player, elements);
            Puts($"[DEBUG] Showed info screen to {player.displayName}");
        }

        private string GetImageId(string imageName)
        {
            if (ImageLibrary == null || !ImageLibrary.IsLoaded)
                return null;

            return (string)ImageLibrary.Call("GetImage", imageName);
        }

        private void DestroyUI(BasePlayer player, string uiName)
        {
            CuiHelper.DestroyUi(player, uiName);

            if (_autoCloseTimers.ContainsKey(player.userID))
            {
                _autoCloseTimers[player.userID]?.Destroy();
                _autoCloseTimers.Remove(player.userID);
            }
        }

        private void DestroyAllUI(BasePlayer player)
        {
            CuiHelper.DestroyUi(player, WelcomeUIName);
            CuiHelper.DestroyUi(player, InfoUIName);

            if (_autoCloseTimers.ContainsKey(player.userID))
            {
                _autoCloseTimers[player.userID]?.Destroy();
                _autoCloseTimers.Remove(player.userID);
            }
        }

        #endregion
    }
}