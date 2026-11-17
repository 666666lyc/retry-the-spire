using System.Collections;
using System.Reflection;

internal static class CompatibilityChecks
{
    private const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
    internal static void Run(Assembly mod, Action<string, Action> check)
    {
        var game = Assembly.Load("sts2");
        Type Game(string name) => game.GetType("MegaCrit.Sts2.Core." + name, true)!;
        Type Mod(string name) => mod.GetType("Retry." + name, true)!;
        object? Call(string type, string method, params object?[] args) => Mod(type).GetMethod(method, All)!.Invoke(null, args);
        object Read(object obj, string name) => obj.GetType().GetProperty(name, All)?.GetValue(obj) ?? obj.GetType().GetField(name, All)!.GetValue(obj)!;
        void Require(bool condition, string message) { if (!condition) throw new Exception(message); }

        var harmonyAssembly = Assembly.Load("0Harmony");
        var harmonyType = harmonyAssembly.GetType("HarmonyLib.Harmony", true)!;
        var harmony = Activator.CreateInstance(harmonyType, "retry.compatibility.target-check")!;
        var processorType = harmonyAssembly.GetType("HarmonyLib.PatchClassProcessor", true)!;
        var getOriginal = harmonyAssembly.GetTypes().SelectMany(t => t.GetMethods(All)).Single(m => m.Name == "GetOriginalMethod" && m.GetParameters().Length == 1 && m.GetParameters()[0].ParameterType.Name == "HarmonyMethod");
        check("all Harmony targets and patch argument signatures resolve", () =>
        {
            foreach (var type in mod.GetTypes().Where(t => t.GetCustomAttributesData().Any(a => a.AttributeType.Name == "HarmonyPatch")))
            {
                var processor = Activator.CreateInstance(processorType, harmony, type)!;
                var bulk = ((IEnumerable)processorType.GetMethod("GetBulkMethods", All)!.Invoke(processor, null)!).Cast<MethodBase>().ToList();
                var patches = (IEnumerable)processorType.GetField("patchMethods", All)!.GetValue(processor)!;
                foreach (var patch in patches)
                {
                    var info = Read(patch, "info");
                    var method = (MethodInfo)Read(info, "method");
                    var targets = bulk.Count != 0 ? bulk : new List<MethodBase> { (MethodBase?)getOriginal.Invoke(null, new[] { info }) ?? throw new Exception(type.Name + ": missing target") };
                    foreach (var original in targets)
                        foreach (var parameter in method.GetParameters())
                        {
                            if (parameter.Name!.StartsWith("__")) continue;
                            var argument = original.GetParameters().SingleOrDefault(p => p.Name == parameter.Name);
                            Require(argument != null, $"{type.Name}: {original.Name} has no argument {parameter.Name}");
                            var expected = parameter.ParameterType.IsByRef ? parameter.ParameterType.GetElementType()! : parameter.ParameterType;
                            var actual = argument!.ParameterType.IsByRef ? argument.ParameterType.GetElementType()! : argument.ParameterType;
                            Require(expected.IsAssignableFrom(actual), $"{type.Name}: incompatible argument {parameter.Name}: {actual} -> {expected}");
                        }
                }
            }
        });
        check("new handshake patch is registered after legacy patches", () =>
        {
            var order = ((IEnumerable<Type>)Call("PatchRegistration", "TypesInOrder", mod)!).Where(t => t.GetCustomAttributesData().Any(a => a.AttributeType.Name == "HarmonyPatch")).ToArray();
            Require(order[^1] == Mod("RestartProtocol_HandshakePatch"), "handshake patch registration changed");
            var target = Game("Multiplayer.NetClientGameService").GetMethod("HandshakeSucceeded")!;
            Require(target.GetParameters()[1].ParameterType == Game("Multiplayer.PeerVersionInfo"), "peer version callback changed");
        });
        check("load lobby listener implements current native interface", () =>
        {
            var implementation = Mod("DirectLoadRunLobbyListener");
            var contract = Game("Multiplayer.Game.Lobby.ILoadRunLobbyListener");
            var map = implementation.GetInterfaceMap(contract);
            Require(map.InterfaceMethods.Length == map.TargetMethods.Length, "incomplete lobby callback implementation");
            Require(implementation.GetMethod("PlayerConnected")!.GetParameters()[0].ParameterType == Game("Entities.Multiplayer.LoadRunLobbyPlayer"), "incorrect player callback");
        });
        check("runtime factories, ascension and custom mode contracts", () =>
        {
            foreach (var field in Mod("GameCompatibility").GetFields(All).Where(f => f.FieldType == typeof(MethodInfo) || f.FieldType == typeof(ConstructorInfo)))
                if (!field.Name.Contains("BlitzMap")) Require(field.GetValue(null) != null, "missing compatibility member " + field.Name);
            var mode = Game("Runs.GameMode");
            bool beta = Enum.GetNames(mode).Contains("Blitz");
            foreach (var name in beta ? new[] { "Standard", "Dread", "Blitz" } : new[] { "Standard", "Custom" })
            {
                var value = Enum.Parse(mode, name);
                Require(Call("GameCompatibility", "RetryMode", value, false)!.ToString() == name, "mode changed: " + name);
                Require(Call("GameCompatibility", "Ascension", value, 0) != null, "ascension factory failed");
            }
            var daily = Call("GameCompatibility", "RetryMode", Enum.Parse(mode, "Daily"), false)!;
            Require(daily.ToString() != "Daily", "daily retry could submit a leaderboard run");
            if (beta) Require(Mod("GameCompatibility").GetField("BlitzMap", All)!.GetValue(null) != null, "Blitz map adapter missing");
        });
        check("shuffle advances native state and preserves every other stream", () =>
        {
            var rngSet = Activator.CreateInstance(Game("Runs.RunRngSet"), "COMPAT12SEED")!;
            var save = rngSet.GetType().GetMethod("ToSerializable")!.Invoke(rngSet, null)!;
            var states = (IDictionary)Read(save, "Rngs");
            var kind = Game("Entities.Rngs.RunRngType");
            var shuffle = Enum.Parse(kind, "Shuffle");
            var original = states[shuffle]!;
            var expected = Activator.CreateInstance(Game("Random.Rng"), original)!;
            expected.GetType().GetMethod("NextUnsignedLong", System.Type.EmptyTypes)!.Invoke(expected, null);
            var expectedState = expected.GetType().GetMethod("ToSerializable")!.Invoke(expected, null)!;
            var others = states.Keys.Cast<object>().Where(k => !k.Equals(shuffle)).ToDictionary(k => k, k => states[k]);
            Call("RoomSaveTransform", "AdvanceShuffle", save, 1);
            Require(expectedState.Equals(states[shuffle]), "shuffle xoshiro state did not advance correctly");
            foreach (var pair in others) Require(ReferenceEquals(pair.Value, states[pair.Key]), "unrelated stream changed: " + pair.Key);
            Call("RoomSaveTransform", "AdvanceShuffle", save, 2);
            Require((int)Read(states[shuffle]!, "counter") == 2, "consecutive reroll counter incorrect");
            Require(!original.Equals(states[shuffle]), "only counter changed, not RNG state");
        });
        check("native snapshot rejects truncated and foreign-build data", () =>
        {
            var header = System.Text.Encoding.ASCII.GetBytes("RTS-NATIVE-RUN-1\n");
            foreach (var bytes in new[] { header, header.Concat(new byte[16]).Concat(new byte[] { 123, 125 }).ToArray() })
            {
                try { Call("NativeRunSnapshot", "Decode", bytes); }
                catch (TargetInvocationException e) when (e.InnerException is InvalidDataException) { continue; }
                throw new Exception("invalid native snapshot accepted");
            }
        });
        check("native run JSON preserves RNG words, mode and custom flag", () =>
        {
            var saveType = Game("Saves.SerializableRun");
            var save = Activator.CreateInstance(saveType)!;
            var rngSet = Activator.CreateInstance(Game("Runs.RunRngSet"), "NATIVE12SEED")!;
            saveType.GetProperty("SerializableRng")!.SetValue(save, rngSet.GetType().GetMethod("ToSerializable")!.Invoke(rngSet, null));
            saveType.GetProperty("StartTime")!.SetValue(save, 123456L);
            saveType.GetProperty("Players")!.SetValue(save, Activator.CreateInstance(saveType.GetProperty("Players")!.PropertyType));
            var modeType = Game("Runs.GameMode");
            var mode = Enum.Parse(modeType, Enum.GetNames(modeType).Contains("Dread") ? "Dread" : "Custom");
            saveType.GetProperty("GameMode")!.SetValue(save, mode);
            saveType.GetProperty("IsCustomGame")?.SetValue(save, true);
            var bytes = (byte[])Call("NativeRunSnapshot", "Encode", save)!;
            var decoded = Call("NativeRunSnapshot", "Decode", bytes)!;
            Require(Read(decoded, "StartTime").Equals(123456L), "run identity lost");
            Require(Read(decoded, "GameMode").Equals(mode), "game mode lost");
            var first = (IDictionary)Read(Read(save, "SerializableRng"), "Rngs");
            var second = (IDictionary)Read(Read(decoded, "SerializableRng"), "Rngs");
            foreach (var key in first.Keys) Require(first[key]!.Equals(second[key]), "RNG state lost: " + key);
            if (saveType.GetProperty("IsCustomGame") != null) Require(Read(decoded, "IsCustomGame") is true, "beta custom flag lost");
            Call("GameCompatibility", "PrepareHistoryMode", decoded);
            Require(Read(decoded, "GameMode").Equals(mode), "custom or Dread history mode changed");
            var dailySave = Activator.CreateInstance(saveType)!;
            saveType.GetProperty("GameMode")!.SetValue(dailySave, Enum.Parse(modeType, "Daily"));
            saveType.GetProperty("DailyTime")!.SetValue(dailySave, DateTimeOffset.UtcNow);
            Call("GameCompatibility", "PrepareHistoryMode", dailySave);
            Require(Read(dailySave, "GameMode").ToString() != "Daily", "native daily retry remained leaderboard eligible");
            Require(saveType.GetProperty("DailyTime")!.GetValue(dailySave) == null, "native daily timestamp retained");
            if (saveType.GetProperty("IsCustomGame") != null) Require(Read(dailySave, "IsCustomGame") is true, "native daily retry lost custom flag");
        });
        check("required private game members remain available", () =>
        {
            foreach (var type in new[] { "StateAccessor", "QuickRoomRestart", "RestartPacketSender", "RestartProtocol_ClientLobbyPatch", "RngSnapshotStore", "SnapshotPersistence", "RunTimeOffset", "HistoryPrePopulator", "EventListPatcher" })
                foreach (var member in Mod(type).GetFields(All).Where(f => f.FieldType == typeof(FieldInfo) || f.FieldType == typeof(PropertyInfo) || f.FieldType == typeof(MethodInfo)))
                    Require(member.GetValue(null) != null, $"Missing game member: {type}.{member.Name}");
        });
        check("v4 migration retains incompatible original player JSON", () =>
        {
            const string oldPlayer = "[{\"net_id\":1,\"rng\":{\"seed\":1,\"counters\":{\"0\":4}}}]";
            var json = System.Text.Json.JsonSerializer.Serialize(new
            {
                version = 4,
                entries = new Dictionary<string, object>
                {
                    ["100|legacy-seed|0|2"] = new { row = 3, col = 1, players_json = oldPlayer }
                }
            });
            using var output = new MemoryStream();
            using (var gzip = new System.IO.Compression.GZipStream(output, System.IO.Compression.CompressionLevel.SmallestSize, true))
                gzip.Write(System.Text.Encoding.UTF8.GetBytes(json));
            var migrated = Call("SnapshotLegacyMigration", "MigrateLegacy", "RTS-SNAPSHOTS-4\n" + Convert.ToBase64String(output.ToArray()))!;
            var blobs = ((IEnumerable)Read(migrated, "Players")).Cast<byte[]>().ToArray();
            Require(blobs.Any(blob => System.Text.Encoding.UTF8.GetString(blob) == "RTS-LEGACY-PLAYER-JSON-4\n" + oldPlayer), "incompatible legacy player data discarded");
        });
    }
}
