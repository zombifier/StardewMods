﻿using HarmonyLib;
using NLua;
using SinZationalSeleneSupport.Support;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewModdingAPI.Framework;
using StardewModdingAPI.Framework.Events;
using StardewModdingAPI.Framework.Logging;
using StardewModdingAPI.Framework.ModHelpers;
using StardewModdingAPI.Framework.ModLoading;
using StardewModdingAPI.Framework.Reflection;
using StardewModdingAPI.Toolkit.Serialization;
using StardewValley;
using StardewValley.Monsters;
using System;
using System.IO;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;

namespace SinZationalSeleneSupport
{
    internal class ModEntry : Mod
    {
        private static bool IsFirstInstantiation = true;
        private static ModEntry OGInstance;
        private static bool HasBootstrapped = false;

        [ModuleInitializer]
        public static void ModuleInitializer()
        {
            var harmony = new Harmony("SinZational.SeleneSupport.Bootstrap.Early");
            harmony.Patch(
                AccessTools.Method(typeof(SCore), "TryLoadModEntry"),
                prefix: new HarmonyMethod(typeof(ModEntry), nameof(ModEntry.TryLoadModEntryPrefix))
            );
        }

        private static bool TryLoadModEntryPrefix(IModMetadata metadata, Assembly modAssembly, ref Mod? mod, ref string? error, ref bool __result)
        {
            if (metadata.Manifest.UniqueID == "SinZational.SeleneSupport")
            {
                mod = new ModEntry();
                error = null;
                __result = true;
                return false;
            }
            return true;
        }

        public ModEntry()
        {
            // Due to SMAPI checking that there is exactly one Mod class in the assembly
            // this ModEntry class is also the ModEntry class for every mod we are impersonating
            // This lets us find out that we are ourselves
            if (IsFirstInstantiation)
            {
                OGInstance = this;
                // Cannot use ModManifest yet as we haven't received it yet
                var harmony = new Harmony("SinZational.SeleneSupport.Bootstrap");
                harmony.Patch(
                    AccessTools.Method(typeof(SCore), "TryLoadMod"),
                    prefix: new HarmonyMethod(typeof(ModEntry), nameof(ModEntry.TryLoadModPrefix))
                );
                IsFirstInstantiation = false;
                NativeLibrary.Load(Path.Combine(Directory.GetParent(typeof(ModEntry).Assembly.Location).FullName, "runtimes", "win-x64", "native", "lua54.dll"));
            }
        }

        private static IntPtr DllImportResolver(string libraryName, Assembly assembly, DllImportSearchPath? searchPath)
        {
            if (libraryName == "lua54")
            {

            }
            return IntPtr.Zero;
        }

        private static bool TryLoadModPrefix(SCore __instance, ref IModMetadata mod, IInterfaceProxyFactory proxyFactory, JsonHelper jsonHelper, ContentCoordinator contentCore, ref ModFailReason? failReason, ref bool __result)
        {
            if (!mod.IsContentPack || mod.Manifest.ContentPackFor?.UniqueID != "SinZ.SeleneSupport")
            {
                return true;
            }

            var manifest = new LuaCompatManifest(mod.Manifest);
            var modMetadata = new ModMetadata(
                    manifest.Name,
                    mod.DirectoryPath,
                    mod.RootPath,
                    manifest,
                    null, //dataRecords (info from metadata.json)
                    false //isIgnored
            );
            var engine = new Lua();
            engine.LoadCLRPackage();
            try
            {
                engine.DoString($"package.path=\"?;?.lua;{Path.Combine(modMetadata.DirectoryPath, "?.lua").Replace('\\', '/')}\"");
                engine.DoFile(Path.Combine(modMetadata.DirectoryPath, "modentry.lua"));
            } catch (Exception ex)
            {
                OGInstance.Monitor.Log("Something blew up while loading lua mod");
            }
            Mod modEntry = engine.GetTable(typeof(LuaMod), "ModEntry") as Mod;
            if (modEntry == null)
            {
                OGInstance.Monitor.Log($"Failed to load {modMetadata.Manifest.Name}: Could not find 'ModEntry' variable or it was not a Mod");
                failReason = ModFailReason.LoadFailed;
                __result = false;
                return false;
            }



            var sCoreType = typeof(SCore);
            var flags = BindingFlags.NonPublic | BindingFlags.Instance;
            var modRegistry = sCoreType.GetField("ModRegistry", flags).GetValue(__instance) as ModRegistry;
            var logManager = sCoreType.GetField("LogManager", flags).GetValue(__instance) as LogManager;
            var eventManager = sCoreType.GetField("EventManager", flags).GetValue(__instance) as EventManager;
            var commandManager = sCoreType.GetField("CommandManager", flags).GetValue(__instance) as CommandManager;
            var reflection = sCoreType.GetField("Reflection", flags).GetValue(__instance) as Reflector;
            var multiplayer = sCoreType.GetField("Multiplayer", flags).GetValue(__instance) as SMultiplayer;

            var createFakeContentPack = sCoreType.GetMethod("CreateFakeContentPack", flags);


            // get content packs
            IContentPack[] GetContentPacks()
            {
                if (!modRegistry.AreAllModsLoaded)
                    throw new InvalidOperationException("Can't access content packs before SMAPI finishes loading mods.");

                return modRegistry
                    .GetAll(assemblyMods: false)
                    .Where(p => p.IsContentPack && modMetadata.HasID(p.Manifest.ContentPackFor!.UniqueID))
                    .Select(p => p.ContentPack!)
                    .ToArray();
            }

            // init mod helpers
            IMonitor monitor = logManager.GetMonitor(manifest.UniqueID, mod.DisplayName);
            TranslationHelper translationHelper = new(modMetadata, contentCore.GetLocale(), contentCore.Language);
            IModHelper modHelper;
            {
                IModEvents events = new ModEvents(mod, eventManager);
                ICommandHelper commandHelper = new CommandHelper(mod, commandManager);
#if !NET6_0
                ContentHelper contentHelper = new(contentCore, mod.DirectoryPath, mod, monitor, reflection);
#endif
                GameContentHelper gameContentHelper = new(contentCore, mod, mod.DisplayName, monitor, reflection);
                IModContentHelper modContentHelper = new ModContentHelper(contentCore, mod.DirectoryPath, mod, mod.DisplayName, gameContentHelper.GetUnderlyingContentManager(), reflection);
                IContentPackHelper contentPackHelper = new ContentPackHelper(
                    mod: mod,
                    contentPacks: new Lazy<IContentPack[]>(GetContentPacks),
                    createContentPack: (dirPath, fakeManifest) => createFakeContentPack.Invoke(__instance, new object[] { dirPath, fakeManifest, contentCore, modMetadata }) as IContentPack
                );
                IDataHelper dataHelper = new DataHelper(mod, mod.DirectoryPath, jsonHelper);
                IReflectionHelper reflectionHelper = new ReflectionHelper(mod, mod.DisplayName, reflection);
                IModRegistry modRegistryHelper = new ModRegistryHelper(mod, modRegistry, proxyFactory, monitor);
                IMultiplayerHelper multiplayerHelper = new MultiplayerHelper(mod, multiplayer);

                modHelper = new ModHelper(mod, mod.DirectoryPath, () => (Game1.game1 as SGame).Input, events,
#if !NET6_0
                    contentHelper,
#endif
                    gameContentHelper, modContentHelper, contentPackHelper, commandHelper, dataHelper, modRegistryHelper, reflectionHelper, multiplayerHelper, translationHelper
                );
            }

            // init mod

            modEntry.ModManifest = manifest;
            modEntry.Helper = modHelper;
            modEntry.Monitor = monitor;
            var luaNativeModEntry = engine["ModEntry"] as LuaTable;
            luaNativeModEntry["ModManifest"] = manifest;
            luaNativeModEntry["Helper"] = modHelper;
            luaNativeModEntry["Monitor"] = monitor;

            // track mod
            modMetadata.SetMod(modEntry, translationHelper);
            modRegistry.Add(modMetadata);
            failReason = null;
            __result = true;

            return false;
        }

        public override void Entry(IModHelper helper)
        {
        }
    }
}
