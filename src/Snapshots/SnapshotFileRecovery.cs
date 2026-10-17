using System;
using System.IO;
using MegaCrit.Sts2.Core.Saves;

namespace Retry;

// File operations are separate from profile discovery and native game singletons.
internal static class SnapshotFileRecovery
{
    internal static SnapshotData? Read(ISaveStore store, string path, out byte[]? payload, out bool migrated, out bool recovered, out Exception? primaryFailure)
    {
        SnapshotData? data = null;
        payload = null;
        migrated = false;
        recovered = false;
        primaryFailure = null;
        if (store.FileExists(path))
        {
            try
            {
                data = SnapshotCodec.Decode(SnapshotPersistence.ReadRequired(store, path), out payload, out migrated);
            }
            catch (Exception ex)
            {
                primaryFailure = ex;
            }
        }

        string backup = path + ".backup";
        if (data == null && store.FileExists(backup))
        {
            data = SnapshotCodec.Decode(SnapshotPersistence.ReadRequired(store, backup), out payload, out migrated);
            recovered = true;
        }

        return data;
    }

    internal static void Write(ISaveStore localStore, string path, string encoded, bool loadedFromBackup)
    {
        string temp = path + ".tmp.backup";
        string backup = path + ".backup";
        if (localStore.FileExists(temp))
            localStore.DeleteFile(temp);
        localStore.WriteFile(temp, encoded);
        if (!localStore.FileExists(temp))
            throw new IOException("temporary snapshot file was not created");
        _ = SnapshotCodec.Decode(SnapshotPersistence.ReadRequired(localStore, temp), out _, out _);
        if (loadedFromBackup)
        {
            if (localStore.FileExists(path))
                localStore.DeleteFile(path);
        }
        else
        {
            if (localStore.FileExists(backup))
                localStore.DeleteFile(backup);
            if (localStore.FileExists(path))
                localStore.RenameFile(path, backup);
        }

        try
        {
            localStore.RenameFile(temp, path);
        }
        catch
        {
            if (localStore.FileExists(backup) && !localStore.FileExists(path))
                localStore.RenameFile(backup, path);
            throw;
        }
    }
}
