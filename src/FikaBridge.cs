using System;
using System.Reflection;
using System.Linq.Expressions;
using BepInEx.Bootstrap;
using EFT;
using HarmonyLib;
using UnityEngine;

namespace ImmersiveVoice
{
    // Optional integration: the DLL has no assembly reference to Fika, so the
    // same artifact also loads in SPT without Fika installed.
    internal sealed class FikaBridge
    {
        private Func<bool> _headless;
        private Func<bool> _isServer;
        private PropertyInfo _downed;
        private Func<Player, bool> _isDowned;
        private Func<Player, bool> _observedAI;
        private Func<Player, int> _netId;
        public Assembly Assembly { get; private set; }
        public MethodInfo Extract { get; private set; }

        public FikaBridge()
        {
            if (!Chainloader.PluginInfos.TryGetValue("com.fika.core", out var fika)) return;
            Assembly assembly = Assembly = fika.Instance.GetType().Assembly;
            Type backend = assembly.GetType("Fika.Core.Main.Utils.FikaBackendUtils", true);
            MethodInfo getter = backend.GetProperty("IsHeadless")?.GetGetMethod();
            if (getter == null) throw new MissingMemberException(backend.FullName, "IsHeadless");
            _headless = (Func<bool>)Delegate.CreateDelegate(typeof(Func<bool>), getter);
            MethodInfo server = backend.GetProperty("IsServer")?.GetGetMethod();
            if (server == null) throw new MissingMemberException(backend.FullName, "IsServer");
            _isServer = (Func<bool>)Delegate.CreateDelegate(typeof(Func<bool>), server);
            Type player = assembly.GetType("Fika.Core.Main.Players.FikaPlayer", true);
            _downed = player.GetProperty("Downed");
            if (_downed == null) throw new MissingMemberException(player.FullName, "Downed");
            // Resolve the optional type once. Frequent peer checks must not box
            // a bool through PropertyInfo.GetValue on every sample.
            var argument = Expression.Parameter(typeof(Player), "player");
            _isDowned = Expression.Lambda<Func<Player, bool>>(
                Expression.Property(Expression.Convert(argument, player), _downed), argument).Compile();
            // Remote bots have no local BotOwner, so Player.IsAI is false.
            // Read Fika's replicated identity once per check, without reflection
            // boxing or a mandatory reference to the optional assembly.
            FieldInfo observedAI = player.GetField("IsObservedAI");
            _netId = Expression.Lambda<Func<Player, int>>(Expression.Field(Expression.Convert(argument, player), "NetId"), argument).Compile();
            if (observedAI == null || observedAI.FieldType != typeof(bool))
                throw new MissingMemberException(player.FullName, "IsObservedAI");
            _observedAI = Expression.Lambda<Func<Player, bool>>(Expression.AndAlso(
                Expression.TypeIs(argument, player),
                Expression.Field(Expression.Convert(argument, player), observedAI)), argument).Compile();
            Type game = assembly.GetType("Fika.Core.Main.GameMode.CoopGame", true);
            Extract = AccessTools.DeclaredMethod(game, "Extract");
            if (Extract == null) throw new MissingMemberException(game.FullName, "Extract");
        }

        public bool Headless => Application.isBatchMode
            || Chainloader.PluginInfos.ContainsKey("com.fika.headless")
            || (_headless != null && _headless());

        public bool Downed(Player player)
        {
            return _downed != null && _downed.DeclaringType.IsInstanceOfType(player)
                && _isDowned(player);
        }

        public bool IsAI(Player player) => player != null
            && (player.IsAI || (_observedAI != null && _observedAI(player)));

        public int NetworkId(Player player) => _netId != null && _downed.DeclaringType.IsInstanceOfType(player) ? _netId(player) : 0;

        public VoiceMode Mode(Player player) => _downed == null || !_downed.DeclaringType.IsInstanceOfType(player)
            ? VoiceMode.Solo : (_isServer() ? VoiceMode.FikaHost : VoiceMode.FikaClient);
    }
}
