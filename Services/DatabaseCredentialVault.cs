using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;

namespace Evania.Services;

/// <summary>
/// Stores database master passwords in Windows Credential Manager. Credential targets are scoped to
/// the normalized database path so each Evania database can have an independent password.
/// </summary>
public static class DatabaseCredentialVault
{
    private const string TargetPrefix = "Evania.Database/";
    private const uint CredentialTypeGeneric = 1;
    private const uint CredentialPersistLocalMachine = 2;
    private const int ErrorNotFound = 1168;

    public static string? Read(string databasePath)
    {
        string target = GetTarget(databasePath);

        if (!CredRead(target, CredentialTypeGeneric, 0, out nint credentialPointer))
        {
            int error = Marshal.GetLastWin32Error();

            if (error == ErrorNotFound)
            {
                return null;
            }

            throw new Win32Exception(error, "The database password could not be read from Windows Credential Manager.");
        }

        try
        {
            Credential credential = Marshal.PtrToStructure<Credential>(credentialPointer);

            return credential.CredentialBlobSize == 0
                ? string.Empty
                : Marshal.PtrToStringUni(credential.CredentialBlob, checked((int)credential.CredentialBlobSize / sizeof(char)));
        }
        finally
        {
            CredFree(credentialPointer);
        }
    }

    public static void Write(string databasePath, string password)
    {
        ArgumentException.ThrowIfNullOrEmpty(password);

        nint passwordPointer = Marshal.StringToCoTaskMemUni(password);

        try
        {
            Credential credential = new()
            {
                Type = CredentialTypeGeneric,
                TargetName = GetTarget(databasePath),
                CredentialBlobSize = checked((uint)(password.Length * sizeof(char))),
                CredentialBlob = passwordPointer,
                Persist = CredentialPersistLocalMachine,
                UserName = Environment.UserName,
            };

            if (!CredWrite(ref credential, 0))
            {
                throw new Win32Exception(
                    Marshal.GetLastWin32Error(),
                    "The database password could not be saved to Windows Credential Manager.");
            }
        }
        finally
        {
            ZeroMemory(passwordPointer, password.Length * sizeof(char));
            Marshal.FreeCoTaskMem(passwordPointer);
        }
    }

    public static void Delete(string databasePath)
    {
        if (CredDelete(GetTarget(databasePath), CredentialTypeGeneric, 0))
        {
            return;
        }

        int error = Marshal.GetLastWin32Error();

        if (error != ErrorNotFound)
        {
            throw new Win32Exception(error, "The database password could not be removed from Windows Credential Manager.");
        }
    }

    private static string GetTarget(string databasePath) =>
        TargetPrefix + Path.GetFullPath(databasePath).ToUpperInvariant();

    private static void ZeroMemory(nint pointer, int byteCount)
    {
        for (int index = 0; index < byteCount; index++)
        {
            Marshal.WriteByte(pointer, index, 0);
        }
    }

    [DllImport("Advapi32.dll", EntryPoint = "CredReadW", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredRead(
        string target,
        uint type,
        uint flags,
        out nint credentialPointer);

    [DllImport("Advapi32.dll", EntryPoint = "CredWriteW", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredWrite(ref Credential credential, uint flags);

    [DllImport("Advapi32.dll", EntryPoint = "CredDeleteW", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredDelete(string target, uint type, uint flags);

    [DllImport("Advapi32.dll")]
    private static extern void CredFree(nint buffer);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct Credential
    {
        public uint Flags;
        public uint Type;
        public string TargetName;
        public string? Comment;
        public long LastWritten;
        public uint CredentialBlobSize;
        public nint CredentialBlob;
        public uint Persist;
        public uint AttributeCount;
        public nint Attributes;
        public string? TargetAlias;
        public string UserName;
    }
}
