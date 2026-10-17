using System.Collections;
using System.Reflection;
using System.Runtime.Loader;
using System.Text;

// Reflection keeps this executable free of game and test-framework dependencies.
// Game assemblies are resolved only from the explicitly supplied installation.
if (args.Length != 4 || (args[0] != "capture" && args[0] != "check"))
{
    Console.Error.WriteLine("Usage: Regression <capture|check> <mod.dll> <game-data-dir> <fixtures-dir>");
    return 2;
}

var mode = args[0];
var assemblyPath = Path.GetFullPath(args[1]);
var gameData = Path.GetFullPath(args[2]);
var fixtures = Path.GetFullPath(args[3]);
AssemblyLoadContext.Default.Resolving += (_, name) =>
{
    var path = Path.Combine(gameData, name.Name + ".dll");
    return File.Exists(path) ? AssemblyLoadContext.Default.LoadFromAssemblyPath(path) : null;
};
var mod = AssemblyLoadContext.Default.LoadFromAssemblyPath(assemblyPath);
var failures = new List<string>();
int checks = 0;
void Check(string name, Action action)
{
    checks++;
    try
    {
        action();
        Console.WriteLine("PASS " + name);
    }
    catch (Exception e)
    {
        failures.Add(name);
        Console.Error.WriteLine("FAIL " + name + ": " + (e.InnerException ?? e).Message);
    }
}

void Equal<T>(T expected, T actual)
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual))
        throw new Exception($"Expected {expected}; actual {actual}");
}

const BindingFlags all = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance;
Type Type(string name) => mod.GetType("Retry." + name) ?? throw new Exception("Missing type " + name);
object Create(string oldName, string newName) => Activator.CreateInstance(mod.GetType("Retry.RngSnapshotStore+" + oldName) ?? Type(newName), true)!;
object Get(object obj, string name) => obj.GetType().GetField(name, all)?.GetValue(obj) ?? obj.GetType().GetProperty(name, all)!.GetValue(obj)!;
void Set(object obj, string name, object value) => obj.GetType().GetField(name, all)!.SetValue(obj, value);
void Add(object obj, string name, object value) => ((IList)Get(obj, name)).Add(value);
object? Call(string type, string method, params object? [] values) => Type(type).GetMethod(method, all)!.Invoke(null, values);
var codec = mod.GetType("Retry.SnapshotCodec") != null ? "SnapshotCodec" : "RngSnapshotStore";
var envelope = mod.GetType("Retry.RestartEnvelope") != null ? "RestartEnvelope" : "RestartRawTransport";
object SyntheticData()
{
    var data = Create("StoreData", "SnapshotData");
    Add(data, "Maps", new byte[] { 0, 1, 127, 128, 255 });
    Add(data, "Maps", Array.Empty<byte>());
    Add(data, "Players", new byte[] { 9, 8, 7 });
    foreach (long start in new long[]
    {
        200,
        100
    }

    )
    {
        var run = Create("RunRecord", "SnapshotRunRecord");
        Set(run, "StartTime", start);
        Set(run, "Seed", start == 100 ? "seed-中文" : "B");
        Set(run, "CapturedAt", start + 5);
        foreach (int index in new[]
        {
            2,
            0
        }

        )
        {
            var act = Create("ActRecord", "SnapshotActRecord");
            Set(act, "Index", index);
            Set(act, "MapBlobId", 0);
            foreach (int floor in new[]
            {
                130,
                0
            }

            )
            {
                var room = Create("RoomRecord", "SnapshotRoomRecord");
                Set(room, "Floor", floor);
                Set(room, "Row", floor - 3);
                Set(room, "Col", 2);
                var counters = (IDictionary)Get(room, "Counters");
                var keyType = counters.GetType().GenericTypeArguments[0];
                counters.Add(Enum.ToObject(keyType, 1), -17);
                counters.Add(Enum.ToObject(keyType, 0), 129);
                Add(room, "PlayerBlobIds", 0);
                Add(act, "Rooms", room);
            }

            Add(run, "Acts", act);
        }

        Add(data, "Runs", run);
    }

    return data;
}

string Surface() => string.Join("\n", mod.GetExportedTypes().SelectMany(t => new[] { "type " + t.FullName }.Concat(t.GetMembers(BindingFlags.Public | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly).Where(m => m.MemberType != MemberTypes.NestedType).Select(m => t.FullName + " " + m.MemberType + " " + m))).Order(StringComparer.Ordinal));
string Patches() => string.Join("\n", mod.GetTypes().SelectMany(t => t.GetCustomAttributesData().Where(a => a.AttributeType.FullName == "HarmonyLib.HarmonyPatch").Select(a => t.FullName + " " + a)).Order(StringComparer.Ordinal));
void Fixture(string name, byte[] bytes)
{
    var path = Path.Combine(fixtures, name);
    if (mode == "capture")
        File.WriteAllBytes(path, bytes);
    else if (!File.ReadAllBytes(path).SequenceEqual(bytes))
        throw new Exception("Fixture mismatch: " + name);
}

Directory.CreateDirectory(fixtures);
Check("public API", () => Fixture("public-api.txt", Encoding.UTF8.GetBytes(Surface())));
Check("Harmony patch targets", () => Fixture("harmony-patches.txt", Encoding.UTF8.GetBytes(Patches())));
Check("Harmony registration order", () =>
{
    var registration = mod.GetType("Retry.PatchRegistration");
    var types = registration == null ? mod.GetTypes() : ((IEnumerable<Type>)registration.GetMethod("TypesInOrder", all)!.Invoke(null, new object[] { mod })!).ToArray();
    var patches = types.Where(t => t.GetCustomAttributesData().Any(a => a.AttributeType.FullName == "HarmonyLib.HarmonyPatch"));
    Fixture("harmony-registration-order.txt", Encoding.UTF8.GetBytes(string.Join("\n", patches.Select(t => t.FullName))));
});
Check("snapshot v5 binary fixture", () => Fixture("snapshot-v5.bin", (byte[])Call(codec, "SerializePayload", SyntheticData())!));
Check("snapshot v5 roundtrip", () =>
{
    var payload = (byte[])Call(codec, "SerializePayload", SyntheticData())!;
    var decoded = Call(codec, "DeserializePayload", payload)!;
    Equal(Convert.ToHexString(payload), Convert.ToHexString((byte[])Call(codec, "SerializePayload", decoded)!));
});
Check("snapshot envelope roundtrip", () =>
{
    object? [] writeArgs =
    {
        SyntheticData(),
        null
    };
    var encoded = (string)Call(codec, "Encode", writeArgs)!;
    object? [] readArgs =
    {
        encoded,
        null,
        false
    };
    var decoded = Call(codec, "Decode", readArgs)!;
    Equal(false, (bool)readArgs[2]!);
    Equal(Convert.ToHexString((byte[])writeArgs[1]!), Convert.ToHexString((byte[])Call(codec, "SerializePayload", decoded)!));
});
Check("snapshot rejects corrupt payloads", () =>
{
    var good = (byte[])Call(codec, "SerializePayload", SyntheticData())!;
    var badMagic = (byte[])good.Clone();
    badMagic[0] ^= 255;
    foreach (var bytes in new[]
    {
        Array.Empty<byte>(),
        badMagic,
        good[..^1],
        good.Concat(new byte[] { 1 }).ToArray()
    }

    )
    {
        bool rejected = false;
        try
        {
            Call(codec, "DeserializePayload", bytes);
        }
        catch (TargetInvocationException)
        {
            rejected = true;
        }

        Equal(true, rejected);
    }
});
Check("raw control envelope fixture", () =>
{
    var kind = Enum.ToObject(Type("RestartRawMessageType"), 1);
    Fixture("control-envelope.bin", (byte[])Call(envelope, "BuildEnvelope", kind, Enumerable.Range(0, 38).Select(x => (byte)x).ToArray())!);
});
Check("raw direct envelope fixture", () =>
{
    var kind = Enum.ToObject(Type("RestartRawMessageType"), 2);
    Fixture("direct-envelope.bin", (byte[])Call(envelope, "BuildEnvelope", kind, Enumerable.Range(0, 100).Select(x => (byte)x).ToArray())!);
});
Check("snapshot v4 migration fixture", () =>
{
    const string json = """{"version":4,"entries":{"100|legacy-seed|0|2":{"row":3,"col":1,"counters":{"0":12},"captured_at":123},"invalid":{"row":0}}}""";
    using var output = new MemoryStream();
    using (var zip = new System.IO.Compression.GZipStream(output, System.IO.Compression.CompressionLevel.SmallestSize, true))
        zip.Write(Encoding.UTF8.GetBytes(json));
    var encoded = "RTS-SNAPSHOTS-4\n" + Convert.ToBase64String(output.ToArray());
    object? [] readArgs =
    {
        encoded,
        null,
        false
    };
    var data = Call(codec, "Decode", readArgs)!;
    Equal(true, (bool)readArgs[2]!);
    Fixture("migrated-v4.bin", (byte[])Call(codec, "SerializePayload", data)!);
    var runs = (IList)Get(data, "Runs");
    Equal(1, runs.Count);
    var act = ((IList)Get(runs[0]!, "Acts"))[0]!;
    var room = ((IList)Get(act, "Rooms"))[0]!;
    Equal(2, (int)Get(room, "Floor"));
    Equal(3, (int)Get(room, "Row"));
});
Check("snapshot compaction fixture", () =>
{
    var data = SyntheticData();
    var storage = mod.GetType("Retry.SnapshotPersistence") ?? Type("RngSnapshotStore");
    var index = mod.GetType("Retry.SnapshotIndex") != null ? "SnapshotIndex" : "RngSnapshotStore";
    storage.GetField("_cache", all)!.SetValue(null, data);
    Call(index, "RebuildHashes");
    Call(index, "GarbageCollectBlobs");
    Fixture("compacted-v5.bin", (byte[])Call(codec, "SerializePayload", data)!);
});
Check("protocol enum values and constants", () =>
{
    var lines = new List<string>();
    foreach (var name in new[]
    {
        "RestartMode",
        "RestartControlKind",
        "RestartRawMessageType"
    }

    )
        foreach (var value in Enum.GetValues(Type(name)))
            lines.Add(name + "." + value + "=" + Convert.ToInt64(value));
    foreach (var item in new[]
    {
        ("RestartRawTransport", "RestartEnvelope", "WireVersion"),
        ("RestartRawTransport", "RestartEnvelope", "HeaderSize"),
        ("RestartRawTransport", "RestartEnvelope", "ControlPayloadSize"),
        ("RestartRawTransport", "RestartEnvelope", "MaxPacketSize"),
        ("RngSnapshotStore", "SnapshotCodec", "SchemaVersion"),
        ("MultiplayerRestartCoordinator", "RestartSession", "PrepareTimeoutSeconds"),
        ("MultiplayerRestartCoordinator", "RestartSession", "ResendSeconds"),
        ("MultiplayerRestartCoordinator", "RestartSession", "DirectResendSeconds"),
        ("MultiplayerRestartCoordinator", "RestartSession", "AutoJoinTimeoutSeconds"),
        ("RestartControlPacket", "RestartControlPacket", "CurrentProtocol")
    }

    )
    {
        var type = mod.GetType("Retry." + item.Item2) ?? Type(item.Item1);
        lines.Add(item.Item3 + "=" + Convert.ToString(type.GetField(item.Item3, all)!.GetRawConstantValue(), System.Globalization.CultureInfo.InvariantCulture));
    }

    Fixture("protocol-constants.txt", Encoding.UTF8.GetBytes(string.Join("\n", lines)));
});
Check("history target boundaries and source identity", () =>
{
    string builder = mod.GetType("Retry.HistoryTargetBuilder") != null ? "HistoryTargetBuilder" : "RetryRunner";
    string multiplayer = mod.GetType("Retry.MultiplayerHistorySaveBuilder") != null ? "MultiplayerHistorySaveBuilder" : "MultiplayerRetryLauncher";
    var historyType = Type(builder).GetMethod("TruncateHistory", all)!.GetParameters()[0].ParameterType;
    var history = Activator.CreateInstance(historyType)!;
    var acts = (IList)Get(history, "MapPointHistory");
    var actType = acts.GetType().GenericTypeArguments[0];
    var entryType = actType.GenericTypeArguments[0];
    for (int act = 0; act < 3; act++)
    {
        var entries = (IList)Activator.CreateInstance(actType)!;
        for (int floor = 0; floor < 2; floor++)
            entries.Add(System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(entryType));
        acts.Add(entries);
    }

    foreach (var pair in new[]
    {
        (builder, "TruncateHistory"),
        (multiplayer, "TruncateHistoryIncludingTarget")
    }

    )
    {
        var result = (IList)Call(pair.Item1, pair.Item2, history, 1, 0)!;
        Equal(2, result.Count);
        Equal(2, ((IList)result[0]!).Count);
        Equal(1, ((IList)result[1]!).Count);
        Equal(true, ReferenceEquals(((IList)acts[1]!)[0], ((IList)result[1]!)[0]));
        Equal(false, ReferenceEquals(acts[0], result[0]));
    }

    Equal(3, acts.Count);
    Equal(2, ((IList)acts[1]!).Count);
    Equal(6, (int)Call(builder, "SumFloors", history)!);
});
Check("historical potion shrink preserves native relocation order", () =>
{
    var snapshot = Activator.CreateInstance(Type("PlayerStateSnapshot"))!;
    Set(snapshot, "MaxPotionSlotCount", 4);
    var potions = (IList)Get(snapshot, "Potions");
    var potionType = potions.GetType().GenericTypeArguments[0];
    void Assign(object target, string name, object value)
    {
        var field = target.GetType().GetField(name, all);
        if (field != null)
            field.SetValue(target, value);
        else
            target.GetType().GetProperty(name, all)!.SetValue(target, value);
    }

    foreach (int slot in new[]
    {
        0,
        2,
        3
    }

    )
    {
        var potion = Activator.CreateInstance(potionType)!;
        Assign(potion, "SlotIndex", slot);
        potions.Add(potion);
    }

    var moved = potions[2];
    var helper = mod.GetType("Retry.HistoricalPotions") != null ? "HistoricalPotions" : "StateReconstructor";
    Call(helper, "AdjustPotionSlotCount", snapshot, -2, "regression", false);
    Equal(2, (int)Get(snapshot, "MaxPotionSlotCount"));
    Equal(2, potions.Count);
    Equal(true, ReferenceEquals(moved, potions[1]));
    Equal(1, (int)Get(moved!, "SlotIndex"));
});
if (mode != "capture")
{
    bool Rejects(Action action)
    {
        try
        {
            action();
            return false;
        }
        catch (TargetInvocationException)
        {
            return true;
        }
    }

    string Encoded(object data)
    {
        object? [] values =
        {
            data,
            null
        };
        return (string)Call(codec, "Encode", values)!;
    }

    object InstanceCall(object target, string method, params object? [] values) => target.GetType().GetMethod(method, all)!.Invoke(target, values)!;
    Check("snapshot deduplication and blob compaction", () =>
    {
        var data = SyntheticData();
        Type("SnapshotPersistence").GetField("_cache", all)!.SetValue(null, data);
        Call("SnapshotIndex", "RebuildHashes");
        Equal(0, (int)Call("SnapshotIndex", "AddMapBlob", new byte[] { 0, 1, 127, 128, 255 })!);
        Equal(2, (int)Call("SnapshotIndex", "AddMapBlob", new byte[] { 42 })!);
        Call("SnapshotIndex", "GarbageCollectBlobs");
        Equal(1, ((IList)Get(data, "Maps")).Count);
        Equal(1, ((IList)Get(data, "Players")).Count);
        Equal(Convert.ToHexString(File.ReadAllBytes(Path.Combine(fixtures, "compacted-v5.bin"))), Convert.ToHexString((byte[])Call(codec, "SerializePayload", data)!));
    });
    Check("retention protects current run and prioritizes orphan history", () =>
    {
        var data = SyntheticData();
        var runs = (IList)Get(data, "Runs");
        var extra = Create("RunRecord", "SnapshotRunRecord");
        Set(extra, "StartTime", 300L);
        Set(extra, "CapturedAt", 1L);
        runs.Add(extra);
        // Run 200 has history and is older than orphan 300; orphan must still be removed first.
        var removed = new List<long>();
        var method = Type("SnapshotRetention").GetMethod("Prune", all)!;
        var callbackType = method.GetParameters()[4].ParameterType;
        var callback = System.Linq.Expressions.Expression.Parameter(callbackType.GenericTypeArguments[0]);
        var record = System.Linq.Expressions.Expression.Field(callback, "StartTime");
        var add = System.Linq.Expressions.Expression.Call(System.Linq.Expressions.Expression.Constant(removed), typeof(List<long>).GetMethod("Add")!, record);
        var listener = System.Linq.Expressions.Expression.Lambda(callbackType, add, callback).Compile();
        method.Invoke(null, new object[] { data, 0, 100L, new HashSet<long> { 200L }, listener });
        Equal("300,200", string.Join(",", removed));
        Equal(1, runs.Count);
        Equal(100L, (long)Get(runs[0]!, "StartTime"));
    });
    Check("snapshot profile transition", () =>
    {
        var persistence = Type("SnapshotPersistence");
        persistence.GetField("_cache", all)!.SetValue(null, SyntheticData());
        persistence.GetField("_loadedFromBackup", all)!.SetValue(null, true);
        Call("SnapshotPersistence", "BeginProfileLoad", "profile-b/saves/retry_the_spire_snapshots.save");
        Equal(0, ((IList)Get(persistence.GetField("_cache", all)!.GetValue(null)!, "Runs")).Count);
        Equal(false, (bool)persistence.GetField("_loadedFromBackup", all)!.GetValue(null)!);
        Equal("profile-b/saves/retry_the_spire_snapshots.save", (string)persistence.GetField("_cachePath", all)!.GetValue(null)!);
    });
    (object Proxy, MemoryStore Store) Store()
    {
        var contract = Type("SnapshotFileRecovery").GetMethod("Read", all)!.GetParameters()[0].ParameterType;
        var proxy = DispatchProxy.Create(contract, typeof(MemoryStore));
        return (proxy, (MemoryStore)proxy);
    }

    Check("corrupt primary recovers backup", () =>
    {
        var(proxy, store) = Store();
        store.Files["save"] = "corrupt";
        store.Files["save.backup"] = Encoded(SyntheticData());
        object? [] values =
        {
            proxy,
            "save",
            null,
            false,
            false,
            null
        };
        var data = Call("SnapshotFileRecovery", "Read", values)!;
        Equal(true, (bool)values[4]!);
        Equal(true, values[5] is Exception);
        Equal(2, ((IList)Get(data, "Runs")).Count);
    });
    Check("valid primary wins and missing files remain empty", () =>
    {
        var(proxy, store) = Store();
        store.Files["save"] = Encoded(SyntheticData());
        store.Files["save.backup"] = "corrupt";
        object? [] values =
        {
            proxy,
            "save",
            null,
            false,
            false,
            null
        };
        var data = Call("SnapshotFileRecovery", "Read", values)!;
        Equal(false, (bool)values[4]!);
        Equal(2, ((IList)Get(data, "Runs")).Count);
        values = new object? []
        {
            proxy,
            "missing",
            null,
            false,
            false,
            null
        };
        Equal<object?>(null, Call("SnapshotFileRecovery", "Read", values));
    });
    Check("verified snapshot rotation preserves old primary", () =>
    {
        var(proxy, store) = Store();
        store.Files["save"] = "old";
        var encoded = Encoded(SyntheticData());
        Call("SnapshotFileRecovery", "Write", proxy, "save", encoded, false);
        Equal(encoded, store.Files["save"]);
        Equal("old", store.Files["save.backup"]);
        Equal(false, store.Files.ContainsKey("save.tmp.backup"));
    });
    Check("recovered backup survives replacement", () =>
    {
        var(proxy, store) = Store();
        store.Files["save"] = "corrupt";
        store.Files["save.backup"] = "known-good";
        Call("SnapshotFileRecovery", "Write", proxy, "save", Encoded(SyntheticData()), true);
        Equal("known-good", store.Files["save.backup"]);
    });
    Check("failed rename restores primary", () =>
    {
        var(proxy, store) = Store();
        store.Files["save"] = "old";
        store.FailRenameFrom = "save.tmp.backup";
        Equal(true, Rejects(() => Call("SnapshotFileRecovery", "Write", proxy, "save", Encoded(SyntheticData()), false)));
        Equal("old", store.Files["save"]);
    });
    Check("invalid temporary snapshot cannot replace primary", () =>
    {
        var(proxy, store) = Store();
        store.Files["save"] = "old";
        Equal(true, Rejects(() => Call("SnapshotFileRecovery", "Write", proxy, "save", "corrupt", false)));
        Equal("old", store.Files["save"]);
    });
    Check("raw envelope validation", () =>
    {
        var validate = Type(envelope).GetMethod("Validate", all)!;
        var reliable = Enum.Parse(validate.GetParameters()[1].ParameterType, "Reliable");
        var unreliable = Enum.Parse(validate.GetParameters()[1].ParameterType, "Unreliable");
        var packet = File.ReadAllBytes(Path.Combine(fixtures, "control-envelope.bin"));
        Equal(38, (int)validate.Invoke(null, new[] { (object)packet, reliable, 0 })!);
        Equal(38, (int)validate.Invoke(null, new[] { (object)packet, reliable, -1 })!);
        var invalid = new List<byte[]>
        {
            packet[..17],
            packet.Concat(new byte[] { 1 }).ToArray(),
            new byte[8 * 1024 * 1024 + 1]
        };
        foreach (var offset in new[]
        {
            16,
            17,
            18,
            20
        }

        )
        {
            var bad = (byte[])packet.Clone();
            bad[offset] = 255;
            invalid.Add(bad);
        }

        foreach (var bad in invalid)
            Equal(true, Rejects(() => validate.Invoke(null, new[] { (object)bad, reliable, 0 })));
        Equal(true, Rejects(() => validate.Invoke(null, new[] { (object)packet, unreliable, 0 })));
        Equal(true, Rejects(() => validate.Invoke(null, new[] { (object)packet, reliable, 1 })));
        Equal(false, (bool)Call(envelope, "HasMagic", new byte[16])!);
    });
    Check("restart attempts reset without clearing connection capabilities", () =>
    {
        var session = Activator.CreateInstance(Type("RestartSession"), true)!;
        var attempt = Get(session, "Attempt");
        var connection = Get(session, "Connection");
        Set(attempt, "_generation", 7U);
        Set(attempt, "_hostId", 9UL);
        Set(attempt, "_nonceHigh", 1UL);
        Set(attempt, "_restartRequested", true);
        var pending = Get(attempt, "PendingProbe");
        InstanceCall(pending, "Add", 9UL);
        InstanceCall(Get(connection, "CapablePeers"), "Add", 9UL);
        InstanceCall(session, "ResetAttempt");
        Equal(0UL, (ulong)Get(attempt, "_hostId"));
        Equal(0, (int)Get(pending, "Count"));
        Equal(7U, (uint)Get(attempt, "_generation"));
        Equal(1, (int)Get(Get(connection, "CapablePeers"), "Count"));
        Equal(false, (bool)Get(attempt, "_restartRequested"));
        InstanceCall(Get(session, "Capabilities"), "ClearNetworkBinding");
        Equal(0, (int)Get(Get(connection, "CapablePeers"), "Count"));
    });
    Check("restart session identity rejects stale packets", () =>
    {
        var session = Activator.CreateInstance(Type("RestartSession"), true)!;
        var rejoin = Activator.CreateInstance(Type("AutoRejoinSession"), all, null, new object? [] { 9UL, 7U, 1UL, 2UL, "old-lobby" }, null)!;
        Equal(true, (bool)InstanceCall(session, "SessionMatches", rejoin, 9UL, 7U, 1UL, 2UL));
        foreach (var values in new object? [][]
        {
            new object? []
            {
                null,
                9UL,
                7U,
                1UL,
                2UL
            },
            new object? []
            {
                rejoin,
                8UL,
                7U,
                1UL,
                2UL
            },
            new object? []
            {
                rejoin,
                9UL,
                6U,
                1UL,
                2UL
            },
            new object? []
            {
                rejoin,
                9UL,
                7U,
                0UL,
                2UL
            },
            new object? []
            {
                rejoin,
                9UL,
                7U,
                1UL,
                0UL
            }
        }

        )
            Equal(false, (bool)InstanceCall(session, "SessionMatches", values));
    });
    Check("browser sessions own independent preview state", () =>
    {
        var first = Activator.CreateInstance(Type("BrowserSession"), true)!;
        var second = Activator.CreateInstance(Type("BrowserSession"), true)!;
        Set(first, "_currentAct", 2);
        Equal(0, (int)Get(second, "_currentAct"));
        Equal(false, ReferenceEquals(Get(first, "_origOutlineModulate"), Get(second, "_origOutlineModulate")));
        foreach (var component in new[]
        {
            "Maps",
            "View",
            "Preview",
            "Commit"
        }

        )
            Equal(false, ReferenceEquals(Get(first, component), Get(second, component)));
    });
}

Console.WriteLine($"{checks - failures.Count}/{checks} checks passed");
return failures.Count == 0 ? 0 : 1;
public class MemoryStore : DispatchProxy
{
    public readonly Dictionary<string, string> Files = new();
    public string? FailRenameFrom;
    protected override object? Invoke(MethodInfo? method, object? []? args)
    {
        args ??= Array.Empty<object?>();
        switch (method!.Name)
        {
            case "FileExists":
                return Files.ContainsKey((string)args[0]!);
            case "ReadFile":
                return Files.GetValueOrDefault((string)args[0]!);
            case "WriteFile":
                Files[(string)args[0]!] = (string)args[1]!;
                return null;
            case "DeleteFile":
                Files.Remove((string)args[0]!);
                return null;
            case "RenameFile":
                var source = (string)args[0]!;
                var target = (string)args[1]!;
                if (source == FailRenameFrom)
                    throw new IOException("Injected rename failure");
                Files.Add(target, Files[source]);
                Files.Remove(source);
                return null;
            default:
                throw new NotSupportedException(method.Name);
        }
    }
}
