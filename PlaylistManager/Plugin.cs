﻿using HarmonyLib;
using IPA;
using IPA.Config;
using IPA.Config.Stores;
using PlaylistManager.Installers;
using SiraUtil.Zenject;
using System.Reflection;
using IPALogger = IPA.Logging.Logger;

namespace PlaylistManager
{
    [Plugin(RuntimeOptions.DynamicInit)]
    public class Plugin
    {
        internal static Plugin Instance { get; private set; }
        internal static IPALogger Log { get; private set; }

        public const string HarmonyId = "com.github.rithik-b.PlaylistManager";
        internal static Harmony harmony;

        [Init]
        /// <summary>
        /// Called when the plugin is first loaded by IPA (either when the game starts or when the plugin is enabled if it starts disabled).
        /// [Init] methods that use a Constructor or called before regular methods like InitWithConfig.
        /// Only use [Init] with one Constructor.
        /// </summary>
        public Plugin(IPALogger logger, Zenjector zenjector)
        {
            Instance = this;
            Log = logger;
            harmony = new Harmony(HarmonyId);
            zenjector.UseMetadataBinder<Plugin>();
            zenjector.UseHttpService();
            zenjector.UseSiraSync(SiraUtil.Web.SiraSync.SiraSyncServiceType.GitHub, "rithik-b");
            zenjector.Install<PlaylistManagerAppInstaller>(Location.App);
            zenjector.Install<PlaylistManagerMenuInstaller>(Location.Menu);
            zenjector.Install<PlaylistManagerGameInstaller>(Location.GameCore);
        }

        #region BSIPA Config
        [Init]
        public void InitWithConfig(Config conf)
        {
            Configuration.PluginConfig.Instance = conf.Generated<Configuration.PluginConfig>();
            Log.Debug("Config loaded");
        }
        #endregion

        [OnEnable]
        public void OnEnable()
        {
            harmony.PatchAll(Assembly.GetExecutingAssembly());
        }

        [OnDisable]
        public void OnDisable()
        {
            harmony.UnpatchSelf();
        }
    }
}
