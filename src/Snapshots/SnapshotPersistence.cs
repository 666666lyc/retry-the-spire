using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using Godot;
using MegaCrit.Sts2.Core.Saves;

namespace Retry;

internal static class SnapshotPersistence
{
    internal const string FileName = "retry_the_spire_snapshots.save";
    internal const string OldTopLevelFileName = "retry_the_spire_rng_snapshots.json";
    internal const int SoftEncodedBytes = 2 * 1024 * 1024;
    internal const int MaxEncodedBytes = 4 * 1024 * 1024;
    internal static readonly FieldInfo? SaveStoreField = typeof(SaveManager).GetField("_saveStore", BindingFlags.Instance | BindingFlags.NonPublic);
    internal static SnapshotData? _cache;
    internal static string? _cachePath;
    internal static bool _loadedFromBackup;
    internal static byte[]? _lastGoodPayload;
    internal static bool TryGetCloudStore(out ISaveStore store, out string path)
    {
        store = null !;
        path = "";
        try
        {
            var manager = SaveManager.Instance;
            if (manager == null || !manager.IsProfileInitialized)
                return false;
            if (SaveStoreField?.GetValue(manager)is not ISaveStore found)
                return false;
            store = found;
            path = manager.GetProfileScopedPath("saves/" + FileName);
            return true;
        }
        catch (Exception ex)
        {
            GD.PrintErr($"{RetryMod.LogPrefix}snapshot cloud store: {ex.Message}");
            return false;
        }
    }

    internal static void EnsureLoaded()
    {
        if (!TryGetCloudStore(out var store, out var path))
        {
            if (_cache == null)
            {
                _cache = new SnapshotData();
                SnapshotIndex.RebuildHashes();
            }

            return;
        }

        if (_cache != null && string.Equals(_cachePath, path, StringComparison.Ordinal))
            return;
        BeginProfileLoad(path);
        bool migrated = false;
        try
        {
            var data = SnapshotFileRecovery.Read(store, path, out var payload, out migrated, out _loadedFromBackup, out var primaryFailure);
            if (_loadedFromBackup)
                GD.PrintErr($"{RetryMod.LogPrefix}snapshot primary unavailable; recovered cloud backup: {primaryFailure?.Message ?? "primary missing"}");
            if (data == null)
            {
                if (primaryFailure != null)
                    throw primaryFailure;
                SnapshotIndex.RebuildHashes();
                return;
            }

            _cache = data;
            SnapshotIndex.RebuildHashes();
            _lastGoodPayload = payload ?? SnapshotCodec.SerializePayload(_cache);
            int rooms = _cache.Runs.Sum(r => r.Acts.Sum(a => a.Rooms.Count));
            GD.Print($"{RetryMod.LogPrefix}snapshot cloud store loaded path={path} runs={_cache.Runs.Count} rooms={rooms} maps={_cache.Maps.Count}");
        }
        catch (Exception ex)
        {
            GD.PrintErr($"{RetryMod.LogPrefix}snapshot load: {ex.Message}; using reconstructed history state");
            _cache = new SnapshotData();
            SnapshotIndex.RebuildHashes();
        }

        if (migrated)
        {
            GD.Print($"{RetryMod.LogPrefix}snapshot schema v4 migrated to compact v{SnapshotCodec.SchemaVersion}");
            TrySave();
        }
    }

    internal static void TrySave()
    {
        if (_cache == null || !TryGetCloudStore(out var store, out var path))
            return;
        ISaveStore localStore = store is CloudSaveStore cloud ? cloud.LocalStore : store;
        string temp = path + ".tmp.backup";
        string backup = path + ".backup";
        try
        {
            string encoded = SnapshotCodec.Encode(_cache, out var payload);
            if (Encoding.UTF8.GetByteCount(encoded) > SoftEncodedBytes)
            {
                SnapshotIndex.PruneToLimit(SoftEncodedBytes);
                encoded = SnapshotCodec.Encode(_cache, out payload);
            }

            int encodedBytes = Encoding.UTF8.GetByteCount(encoded);
            if (encodedBytes > MaxEncodedBytes)
            {
                if (_lastGoodPayload != null)
                {
                    _cache = SnapshotCodec.DeserializePayload(_lastGoodPayload);
                    SnapshotIndex.RebuildHashes();
                }

                GD.PrintErr($"{RetryMod.LogPrefix}snapshot current run exceeds 4 MiB; newest capture was not persisted");
                return;
            }

            SnapshotFileRecovery.Write(localStore, path, encoded, _loadedFromBackup);
            if (store is CloudSaveStore cloudStore)
            {
                cloudStore.CloudStore.WriteFile(path, encoded);
                localStore.SetLastModifiedTime(path, cloudStore.CloudStore.GetLastModifiedTime(path));
            }

            _lastGoodPayload = payload;
            _loadedFromBackup = false;
            DeleteOldTopLevelStore();
            GD.Print($"{RetryMod.LogPrefix}snapshot write path={path} encodedBytes={encodedBytes} payloadBytes={payload.Length} runs={_cache.Runs.Count}");
        }
        catch (Exception ex)
        {
            GD.PrintErr($"{RetryMod.LogPrefix}snapshot save: {ex.Message}");
            try
            {
                if (localStore.FileExists(temp))
                    localStore.DeleteFile(temp);
            }
            catch
            {
            }
        }
    }

    internal static string ReadRequired(ISaveStore store, string path) => store.ReadFile(path) ?? throw new IOException($"snapshot file {path} could not be read");
    internal static void DeleteOldTopLevelStore()
    {
        try
        {
            string oldPath = ProjectSettings.GlobalizePath("user://" + OldTopLevelFileName);
            if (!File.Exists(oldPath))
                return;
            File.Delete(oldPath);
            GD.Print($"{RetryMod.LogPrefix}removed obsolete top-level RNG snapshot store");
        }
        catch (Exception ex)
        {
            GD.PrintErr($"{RetryMod.LogPrefix}remove obsolete RNG snapshot store: {ex.Message}");
        }
    }

    internal static void BeginProfileLoad(string path)
    {
        _cachePath = path;
        _cache = new SnapshotData();
        _loadedFromBackup = false;
    }
}
