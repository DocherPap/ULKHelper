using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace ULKHelper.Automation;

internal static class DiscordCredentials
{
    [StructLayout(LayoutKind.Sequential)]
    private struct Blob
    {
        public int Size;
        public nint Data;
    }

    [DllImport("crypt32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptProtectData(ref Blob input, nint description, nint entropy,
        nint reserved, nint prompt, uint flags, out Blob output);

    [DllImport("crypt32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptUnprotectData(ref Blob input, nint description, nint entropy,
        nint reserved, nint prompt, uint flags, out Blob output);

    [DllImport("kernel32.dll")]
    private static extern nint LocalFree(nint memory);

    public static void Save(string path, string owner, string token)
    {
        var plain = Encoding.UTF8.GetBytes(owner + "\n" + token);
        try
        {
            var encrypted = Transform(plain, protect: true);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.WriteAllBytes(temporary, encrypted);
                File.Move(temporary, path, overwrite: true);
            }
            finally { File.Delete(temporary); }
        }
        finally { CryptographicOperations.ZeroMemory(plain); }
    }

    public static (string Owner, string Token) Read(string path)
    {
        var plain = Transform(File.ReadAllBytes(path), protect: false);
        try
        {
            var parts = Encoding.UTF8.GetString(plain).Split('\n', 2);
            if (parts is not [var owner, var token] || !ulong.TryParse(owner, out var id) || id == 0 || token.Length == 0)
                throw new InvalidDataException("Invalid Discord credentials.");
            return (owner, token);
        }
        finally { CryptographicOperations.ZeroMemory(plain); }
    }

    private static unsafe byte[] Transform(byte[] bytes, bool protect)
    {
        fixed (byte* buffer = bytes)
        {
            var input = new Blob { Size = bytes.Length, Data = (nint)buffer };
            const uint forbidUi = 1;
            var success = protect
                ? CryptProtectData(ref input, 0, 0, 0, 0, forbidUi, out var output)
                : CryptUnprotectData(ref input, 0, 0, 0, 0, forbidUi, out output);
            if (!success) throw new Win32Exception(Marshal.GetLastWin32Error());
            try
            {
                return new ReadOnlySpan<byte>((void*)output.Data, output.Size).ToArray();
            }
            finally
            {
                if (!protect) CryptographicOperations.ZeroMemory(new Span<byte>((void*)output.Data, output.Size));
                LocalFree(output.Data);
            }
        }
    }
}
