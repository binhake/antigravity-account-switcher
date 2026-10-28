using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.IO;
using System.Net;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using Microsoft.Win32;

[assembly: AssemblyTitle("Antigravity Account Switcher")]
[assembly: AssemblyDescription("Lưu và chuyển nhanh tài khoản Google cho Antigravity 2.0 / Antigravity IDE")]
[assembly: AssemblyCompany("Binhake")]
[assembly: AssemblyProduct("Antigravity Account Switcher")]
[assembly: AssemblyCopyright("Copyright © 2026 Binhake. MIT License.")]
[assembly: AssemblyVersion("1.0.0.0")]
[assembly: AssemblyFileVersion("1.0.0.0")]

namespace AntigravityAccountSwitcher
{
    // =========================================================================
    // Native Windows Credential Manager (advapi32.dll) P/Invoke
    // =========================================================================
    internal static class NativeCred
    {
        public const uint CRED_TYPE_GENERIC = 1;
        public const uint CRED_PERSIST_LOCAL_MACHINE = 2;
        public const string TARGET_NAME = "gemini:antigravity";
        public const string USER_NAME = "antigravity";

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct CREDENTIAL
        {
            public uint Flags;
            public uint Type;
            public IntPtr TargetName;
            public IntPtr Comment;
            public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;
            public uint CredentialBlobSize;
            public IntPtr CredentialBlob;
            public uint Persist;
            public uint AttributeCount;
            public IntPtr Attributes;
            public IntPtr TargetAlias;
            public IntPtr UserName;
        }

        [DllImport("advapi32.dll", EntryPoint = "CredReadW", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern bool CredRead(string target, uint type, int reservedFlag, out IntPtr credentialPtr);

        [DllImport("advapi32.dll", EntryPoint = "CredWriteW", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern bool CredWrite([In] ref CREDENTIAL userCredential, [In] uint flags);

        [DllImport("advapi32.dll", EntryPoint = "CredDeleteW", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern bool CredDelete(string target, uint type, int flags);

        [DllImport("advapi32.dll", SetLastError = true)]
        public static extern bool CredFree([In] IntPtr cred);

        public static string ReadAntigravityCredential(out int win32Error)
        {
            win32Error = 0;
            IntPtr credPtr = IntPtr.Zero;
            if (!CredRead(TARGET_NAME, CRED_TYPE_GENERIC, 0, out credPtr))
            {
                win32Error = Marshal.GetLastWin32Error();
                return null;
            }
            try
            {
                CREDENTIAL cred = (CREDENTIAL)Marshal.PtrToStructure(credPtr, typeof(CREDENTIAL));
                if (cred.CredentialBlob == IntPtr.Zero || cred.CredentialBlobSize == 0)
                {
                    return null;
                }
                byte[] bytes = new byte[cred.CredentialBlobSize];
                Marshal.Copy(cred.CredentialBlob, bytes, 0, (int)cred.CredentialBlobSize);
                return Encoding.UTF8.GetString(bytes);
            }
            finally
            {
                CredFree(credPtr);
            }
        }

        public static bool WriteAntigravityCredential(string jsonBlob, out int win32Error)
        {
            win32Error = 0;
            byte[] bytes = Encoding.UTF8.GetBytes(jsonBlob);
            IntPtr targetPtr = Marshal.StringToCoTaskMemUni(TARGET_NAME);
            IntPtr userPtr = Marshal.StringToCoTaskMemUni(USER_NAME);
            IntPtr blobPtr = Marshal.AllocCoTaskMem(bytes.Length);

            try
            {
                Marshal.Copy(bytes, 0, blobPtr, bytes.Length);
                CREDENTIAL cred = new CREDENTIAL();
                cred.Flags = 0;
                cred.Type = CRED_TYPE_GENERIC;
                cred.TargetName = targetPtr;
                cred.Comment = IntPtr.Zero;
                cred.CredentialBlobSize = (uint)bytes.Length;
                cred.CredentialBlob = blobPtr;
                cred.Persist = CRED_PERSIST_LOCAL_MACHINE;
                cred.AttributeCount = 0;
                cred.Attributes = IntPtr.Zero;
                cred.TargetAlias = IntPtr.Zero;
                cred.UserName = userPtr;

                if (!CredWrite(ref cred, 0))
                {
                    win32Error = Marshal.GetLastWin32Error();
                    return false;
                }
                return true;
            }
            finally
            {
                Marshal.FreeCoTaskMem(targetPtr);
                Marshal.FreeCoTaskMem(userPtr);
                Marshal.FreeCoTaskMem(blobPtr);
            }
        }
    }

    // =========================================================================
    // Native Windows SQLite (winsqlite3.dll) & Protobuf Codec for Antigravity IDE
    // =========================================================================
    internal static class NativeSqlite
    {
        private const int SQLITE_OK = 0;
        private const int SQLITE_ROW = 100;
        private const int SQLITE_DONE = 101;
        private const int SQLITE_OPEN_READONLY = 0x00000001;
        private const int SQLITE_OPEN_READWRITE = 0x00000002;
        private static readonly IntPtr SQLITE_TRANSIENT = new IntPtr(-1);

        [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Winapi)]
        private static extern int sqlite3_open_v2(byte[] filenameUtf8, out IntPtr db, int flags, IntPtr zvfs);

        [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Winapi)]
        private static extern int sqlite3_close_v2(IntPtr db);

        [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Winapi)]
        private static extern int sqlite3_busy_timeout(IntPtr db, int ms);

        [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Winapi)]
        private static extern int sqlite3_prepare_v2(IntPtr db, byte[] sqlUtf8, int nByte, out IntPtr stmt, IntPtr pzTail);

        [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Winapi)]
        private static extern int sqlite3_bind_text(IntPtr stmt, int index, byte[] valUtf8, int nByte, IntPtr destructor);

        [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Winapi)]
        private static extern int sqlite3_step(IntPtr stmt);

        [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Winapi)]
        private static extern IntPtr sqlite3_column_blob(IntPtr stmt, int iCol);

        [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Winapi)]
        private static extern int sqlite3_column_bytes(IntPtr stmt, int iCol);

        [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Winapi)]
        private static extern int sqlite3_finalize(IntPtr stmt);

        private static byte[] ToNullTerminatedUtf8(string s)
        {
            byte[] raw = Encoding.UTF8.GetBytes(s ?? "");
            byte[] buf = new byte[raw.Length + 1];
            Buffer.BlockCopy(raw, 0, buf, 0, raw.Length);
            return buf;
        }

        public static string ReadItemValue(string dbPath, string key, out string errorMsg)
        {
            errorMsg = null;
            if (string.IsNullOrEmpty(dbPath) || !File.Exists(dbPath))
            {
                errorMsg = "Không tìm thấy file state.vscdb của Antigravity IDE.";
                return null;
            }

            IntPtr db = IntPtr.Zero;
            IntPtr stmt = IntPtr.Zero;
            try
            {
                int rc = sqlite3_open_v2(ToNullTerminatedUtf8(dbPath), out db, SQLITE_OPEN_READONLY, IntPtr.Zero);
                if (rc != SQLITE_OK || db == IntPtr.Zero)
                {
                    errorMsg = "Không thể mở state.vscdb (mã SQLite: " + rc + ").";
                    return null;
                }
                sqlite3_busy_timeout(db, 3000);

                byte[] sql = ToNullTerminatedUtf8("SELECT value FROM ItemTable WHERE key = ?1;");
                rc = sqlite3_prepare_v2(db, sql, sql.Length, out stmt, IntPtr.Zero);
                if (rc != SQLITE_OK || stmt == IntPtr.Zero)
                {
                    errorMsg = "Lỗi prepare câu lệnh đọc state.vscdb (mã SQLite: " + rc + ").";
                    return null;
                }

                byte[] keyBytes = Encoding.UTF8.GetBytes(key);
                sqlite3_bind_text(stmt, 1, keyBytes, keyBytes.Length, SQLITE_TRANSIENT);

                int stepRc = sqlite3_step(stmt);
                if (stepRc == SQLITE_ROW)
                {
                    int len = sqlite3_column_bytes(stmt, 0);
                    if (len <= 0) return "";
                    IntPtr ptr = sqlite3_column_blob(stmt, 0);
                    if (ptr == IntPtr.Zero) return "";
                    byte[] data = new byte[len];
                    Marshal.Copy(ptr, data, 0, len);
                    return Encoding.UTF8.GetString(data);
                }
                return null;
            }
            catch (Exception ex)
            {
                errorMsg = "Lỗi đọc SQLite (winsqlite3.dll): " + ex.Message;
                return null;
            }
            finally
            {
                if (stmt != IntPtr.Zero) sqlite3_finalize(stmt);
                if (db != IntPtr.Zero) sqlite3_close_v2(db);
            }
        }

        public static bool WriteItemValues(string dbPath, Dictionary<string, string> items, out string errorMsg)
        {
            errorMsg = null;
            if (string.IsNullOrEmpty(dbPath) || !File.Exists(dbPath))
            {
                errorMsg = "Không tìm thấy file state.vscdb của Antigravity IDE.";
                return false;
            }

            IntPtr db = IntPtr.Zero;
            try
            {
                int rc = sqlite3_open_v2(ToNullTerminatedUtf8(dbPath), out db, SQLITE_OPEN_READWRITE, IntPtr.Zero);
                if (rc != SQLITE_OK || db == IntPtr.Zero)
                {
                    errorMsg = "Không thể mở ghi state.vscdb (mã SQLite: " + rc + ").";
                    return false;
                }
                sqlite3_busy_timeout(db, 5000);

                byte[] sql = ToNullTerminatedUtf8("INSERT OR REPLACE INTO ItemTable (key, value) VALUES (?1, ?2);");
                foreach (KeyValuePair<string, string> kv in items)
                {
                    IntPtr stmt = IntPtr.Zero;
                    try
                    {
                        rc = sqlite3_prepare_v2(db, sql, sql.Length, out stmt, IntPtr.Zero);
                        if (rc != SQLITE_OK || stmt == IntPtr.Zero)
                        {
                            errorMsg = "Lỗi prepare INSERT state.vscdb (mã SQLite: " + rc + ").";
                            return false;
                        }
                        byte[] kBytes = Encoding.UTF8.GetBytes(kv.Key);
                        byte[] vBytes = Encoding.UTF8.GetBytes(kv.Value ?? "");
                        sqlite3_bind_text(stmt, 1, kBytes, kBytes.Length, SQLITE_TRANSIENT);
                        sqlite3_bind_text(stmt, 2, vBytes, vBytes.Length, SQLITE_TRANSIENT);
                        int stepRc = sqlite3_step(stmt);
                        if (stepRc != SQLITE_DONE)
                        {
                            errorMsg = string.Format("Lỗi ghi khóa '{0}' vào state.vscdb (mã SQLite: {1}).", kv.Key, stepRc);
                            return false;
                        }
                    }
                    finally
                    {
                        if (stmt != IntPtr.Zero) sqlite3_finalize(stmt);
                    }
                }
                return true;
            }
            catch (Exception ex)
            {
                errorMsg = "Lỗi ghi SQLite (winsqlite3.dll): " + ex.Message;
                return false;
            }
            finally
            {
                if (db != IntPtr.Zero) sqlite3_close_v2(db);
            }
        }
    }

    internal static class IdeTokenCodec
    {
        private static void WriteVarint(MemoryStream ms, ulong val)
        {
            while (val > 0x7FUL)
            {
                ms.WriteByte((byte)((val & 0x7FUL) | 0x80UL));
                val >>= 7;
            }
            ms.WriteByte((byte)(val & 0x7FUL));
        }

        private static byte[] EncodeFieldVarint(int fieldNumber, ulong val)
        {
            using (MemoryStream ms = new MemoryStream())
            {
                WriteVarint(ms, (ulong)((fieldNumber << 3) | 0));
                WriteVarint(ms, val);
                return ms.ToArray();
            }
        }

        private static byte[] EncodeFieldBytes(int fieldNumber, byte[] data)
        {
            using (MemoryStream ms = new MemoryStream())
            {
                WriteVarint(ms, (ulong)((fieldNumber << 3) | 2));
                WriteVarint(ms, (ulong)data.Length);
                ms.Write(data, 0, data.Length);
                return ms.ToArray();
            }
        }

        private static byte[] EncodeFieldString(int fieldNumber, string s)
        {
            return EncodeFieldBytes(fieldNumber, Encoding.UTF8.GetBytes(s ?? ""));
        }

        private static byte[] ConcatBytes(params byte[][] arrays)
        {
            using (MemoryStream ms = new MemoryStream())
            {
                foreach (byte[] a in arrays)
                {
                    if (a != null && a.Length > 0) ms.Write(a, 0, a.Length);
                }
                return ms.ToArray();
            }
        }

        public static string EncodeIdeOAuthTokenValue(string accessToken, string tokenType, string refreshToken, long expirySeconds)
        {
            byte[] tsProto = EncodeFieldVarint(1, (ulong)Math.Max(0L, expirySeconds));
            byte[] tokProto = ConcatBytes(
                EncodeFieldString(1, accessToken ?? ""),
                EncodeFieldString(2, string.IsNullOrEmpty(tokenType) ? "Bearer" : tokenType),
                EncodeFieldString(3, refreshToken ?? ""),
                EncodeFieldBytes(4, tsProto)
            );
            string tokB64 = Convert.ToBase64String(tokProto);

            string authStateJson = "{\"state\":\"signedIn\",\"context\":{\"project\":\"\",\"showProjectError\":false,\"errorMessage\":\"\",\"ineligibleMessage\":\"\",\"verificationUrl\":\"\",\"isGcpTos\":false,\"browserOpenFailed\":false,\"appealUrl\":\"\",\"appealLinkText\":\"\"}}";
            byte[] entry1 = ConcatBytes(
                EncodeFieldString(1, "authStateWithContextSentinelKey"),
                EncodeFieldBytes(2, EncodeFieldString(1, authStateJson))
            );
            byte[] entry2 = ConcatBytes(
                EncodeFieldString(1, "oauthTokenInfoSentinelKey"),
                EncodeFieldBytes(2, EncodeFieldString(1, tokB64))
            );
            byte[] outer = ConcatBytes(
                EncodeFieldBytes(1, entry1),
                EncodeFieldBytes(1, entry2)
            );
            return Convert.ToBase64String(outer);
        }

        private static ulong ReadVarint(byte[] buf, ref int pos)
        {
            ulong val = 0;
            int shift = 0;
            while (pos < buf.Length)
            {
                byte b = buf[pos++];
                val |= (ulong)(b & 0x7F) << shift;
                if ((b & 0x80) == 0) break;
                shift += 7;
            }
            return val;
        }

        public static bool DecodeIdeOAuthTokenValue(string outerB64, out string accessToken, out string tokenType, out string refreshToken, out long expirySeconds)
        {
            accessToken = null;
            tokenType = "Bearer";
            refreshToken = null;
            expirySeconds = 0;
            if (string.IsNullOrEmpty(outerB64)) return false;

            try
            {
                byte[] outer = Convert.FromBase64String(outerB64.Trim());
                int pos = 0;
                while (pos < outer.Length)
                {
                    ulong tag = ReadVarint(outer, ref pos);
                    int fn = (int)(tag >> 3);
                    int wt = (int)(tag & 7);
                    if (wt == 2)
                    {
                        int len = (int)ReadVarint(outer, ref pos);
                        if (len < 0 || pos + len > outer.Length) break;
                        byte[] entry = new byte[len];
                        Buffer.BlockCopy(outer, pos, entry, 0, len);
                        pos += len;

                        if (fn == 1)
                        {
                            string entryKey = null;
                            string innerB64 = null;
                            int ePos = 0;
                            while (ePos < entry.Length)
                            {
                                ulong eTag = ReadVarint(entry, ref ePos);
                                int eFn = (int)(eTag >> 3);
                                int eWt = (int)(eTag & 7);
                                if (eWt == 2)
                                {
                                    int eLen = (int)ReadVarint(entry, ref ePos);
                                    if (eLen < 0 || ePos + eLen > entry.Length) break;
                                    if (eFn == 1)
                                    {
                                        entryKey = Encoding.UTF8.GetString(entry, ePos, eLen);
                                    }
                                    else if (eFn == 2)
                                    {
                                        int vPos = ePos;
                                        int vEnd = ePos + eLen;
                                        while (vPos < vEnd)
                                        {
                                            ulong vTag = ReadVarint(entry, ref vPos);
                                            int vFn = (int)(vTag >> 3);
                                            int vWt = (int)(vTag & 7);
                                            if (vWt == 2)
                                            {
                                                int vLen = (int)ReadVarint(entry, ref vPos);
                                                if (vLen >= 0 && vPos + vLen <= vEnd && vFn == 1)
                                                {
                                                    innerB64 = Encoding.UTF8.GetString(entry, vPos, vLen);
                                                }
                                                vPos += vLen;
                                            }
                                            else if (vWt == 0) ReadVarint(entry, ref vPos);
                                            else break;
                                        }
                                    }
                                    ePos += eLen;
                                }
                                else if (eWt == 0) ReadVarint(entry, ref ePos);
                                else break;
                            }

                            if (entryKey == "oauthTokenInfoSentinelKey" && !string.IsNullOrEmpty(innerB64))
                            {
                                byte[] tok = Convert.FromBase64String(innerB64);
                                int tPos = 0;
                                while (tPos < tok.Length)
                                {
                                    ulong tTag = ReadVarint(tok, ref tPos);
                                    int tFn = (int)(tTag >> 3);
                                    int tWt = (int)(tTag & 7);
                                    if (tWt == 2)
                                    {
                                        int tLen = (int)ReadVarint(tok, ref tPos);
                                        if (tLen < 0 || tPos + tLen > tok.Length) break;
                                        if (tFn == 1) accessToken = Encoding.UTF8.GetString(tok, tPos, tLen);
                                        else if (tFn == 2) tokenType = Encoding.UTF8.GetString(tok, tPos, tLen);
                                        else if (tFn == 3) refreshToken = Encoding.UTF8.GetString(tok, tPos, tLen);
                                        else if (tFn == 4)
                                        {
                                            int sPos = tPos;
                                            int sEnd = tPos + tLen;
                                            while (sPos < sEnd)
                                            {
                                                ulong sTag = ReadVarint(tok, ref sPos);
                                                int sFn = (int)(sTag >> 3);
                                                int sWt = (int)(sTag & 7);
                                                if (sWt == 0)
                                                {
                                                    ulong sec = ReadVarint(tok, ref sPos);
                                                    if (sFn == 1) expirySeconds = (long)sec;
                                                }
                                                else break;
                                            }
                                        }
                                        tPos += tLen;
                                    }
                                    else if (tWt == 0) ReadVarint(tok, ref tPos);
                                    else break;
                                }
                            }
                        }
                    }
                    else if (wt == 0) ReadVarint(outer, ref pos);
                    else break;
                }
                return !string.IsNullOrEmpty(refreshToken);
            }
            catch
            {
                return false;
            }
        }

        public static void DecodeIdeUserStatus(string outerB64, out string email, out string displayName, out string pictureUrl)
        {
            email = null;
            displayName = null;
            pictureUrl = null;
            if (string.IsNullOrEmpty(outerB64)) return;

            try
            {
                byte[] outer = Convert.FromBase64String(outerB64.Trim());
                int pos = 0;
                while (pos < outer.Length)
                {
                    ulong tag = ReadVarint(outer, ref pos);
                    int fn = (int)(tag >> 3);
                    int wt = (int)(tag & 7);
                    if (wt == 2)
                    {
                        int len = (int)ReadVarint(outer, ref pos);
                        if (len < 0 || pos + len > outer.Length) break;
                        if (fn == 1)
                        {
                            int ePos = pos;
                            int eEnd = pos + len;
                            string entryKey = null;
                            string innerB64 = null;
                            while (ePos < eEnd)
                            {
                                ulong eTag = ReadVarint(outer, ref ePos);
                                int eFn = (int)(eTag >> 3);
                                int eWt = (int)(eTag & 7);
                                if (eWt == 2)
                                {
                                    int eLen = (int)ReadVarint(outer, ref ePos);
                                    if (eLen < 0 || ePos + eLen > eEnd) break;
                                    if (eFn == 1) entryKey = Encoding.UTF8.GetString(outer, ePos, eLen);
                                    else if (eFn == 2)
                                    {
                                        int vPos = ePos;
                                        int vEnd = ePos + eLen;
                                        while (vPos < vEnd)
                                        {
                                            ulong vTag = ReadVarint(outer, ref vPos);
                                            int vFn = (int)(vTag >> 3);
                                            int vWt = (int)(vTag & 7);
                                            if (vWt == 2)
                                            {
                                                int vLen = (int)ReadVarint(outer, ref vPos);
                                                if (vLen >= 0 && vPos + vLen <= vEnd && vFn == 1)
                                                {
                                                    innerB64 = Encoding.UTF8.GetString(outer, vPos, vLen);
                                                }
                                                vPos += vLen;
                                            }
                                            else if (vWt == 0) ReadVarint(outer, ref vPos);
                                            else break;
                                        }
                                    }
                                    ePos += eLen;
                                }
                                else if (eWt == 0) ReadVarint(outer, ref ePos);
                                else break;
                            }

                            if (entryKey == "userStatusSentinelKey" && !string.IsNullOrEmpty(innerB64))
                            {
                                byte[] uProto = Convert.FromBase64String(innerB64);
                                int uPos = 0;
                                while (uPos < uProto.Length)
                                {
                                    ulong uTag = ReadVarint(uProto, ref uPos);
                                    int uFn = (int)(uTag >> 3);
                                    int uWt = (int)(uTag & 7);
                                    if (uWt == 2)
                                    {
                                        int uLen = (int)ReadVarint(uProto, ref uPos);
                                        if (uLen < 0 || uPos + uLen > uProto.Length) break;
                                        if (uFn == 3) displayName = Encoding.UTF8.GetString(uProto, uPos, uLen);
                                        else if (uFn == 7) email = Encoding.UTF8.GetString(uProto, uPos, uLen);
                                        else if (uFn == 38) pictureUrl = Encoding.UTF8.GetString(uProto, uPos, uLen);
                                        uPos += uLen;
                                    }
                                    else if (uWt == 0) ReadVarint(uProto, ref uPos);
                                    else break;
                                }
                            }
                        }
                        pos += len;
                    }
                    else if (wt == 0) ReadVarint(outer, ref pos);
                    else break;
                }
            }
            catch { }
        }

        // Patches field 3 (name), field 7 (email), field 38 (pictureUrl) inside antigravityUnifiedStateSync.userStatus
        public static string PatchIdeUserStatus(string existingOuterB64, string email, string displayName, string pictureUrl)
        {
            if (string.IsNullOrEmpty(existingOuterB64)) return null;
            try
            {
                byte[] outer = Convert.FromBase64String(existingOuterB64.Trim());
                int pos = 0;
                string innerB64 = null;
                while (pos < outer.Length)
                {
                    ulong tag = ReadVarint(outer, ref pos);
                    int fn = (int)(tag >> 3);
                    int wt = (int)(tag & 7);
                    if (wt == 2)
                    {
                        int len = (int)ReadVarint(outer, ref pos);
                        if (len < 0 || pos + len > outer.Length) break;
                        if (fn == 1)
                        {
                            int ePos = pos;
                            int eEnd = pos + len;
                            string entryKey = null;
                            while (ePos < eEnd)
                            {
                                ulong eTag = ReadVarint(outer, ref ePos);
                                int eFn = (int)(eTag >> 3);
                                int eWt = (int)(eTag & 7);
                                if (eWt == 2)
                                {
                                    int eLen = (int)ReadVarint(outer, ref ePos);
                                    if (eLen < 0 || ePos + eLen > eEnd) break;
                                    if (eFn == 1) entryKey = Encoding.UTF8.GetString(outer, ePos, eLen);
                                    else if (eFn == 2 && entryKey == "userStatusSentinelKey")
                                    {
                                        int vPos = ePos;
                                        int vEnd = ePos + eLen;
                                        while (vPos < vEnd)
                                        {
                                            ulong vTag = ReadVarint(outer, ref vPos);
                                            int vFn = (int)(vTag >> 3);
                                            int vWt = (int)(vTag & 7);
                                            if (vWt == 2)
                                            {
                                                int vLen = (int)ReadVarint(outer, ref vPos);
                                                if (vLen >= 0 && vPos + vLen <= vEnd && vFn == 1)
                                                {
                                                    innerB64 = Encoding.UTF8.GetString(outer, vPos, vLen);
                                                }
                                                vPos += vLen;
                                            }
                                            else if (vWt == 0) ReadVarint(outer, ref vPos);
                                            else break;
                                        }
                                    }
                                    ePos += eLen;
                                }
                                else if (eWt == 0) ReadVarint(outer, ref ePos);
                                else break;
                            }
                        }
                        pos += len;
                    }
                    else if (wt == 0) ReadVarint(outer, ref pos);
                    else break;
                }

                if (string.IsNullOrEmpty(innerB64)) return null;
                byte[] uProto = Convert.FromBase64String(innerB64);
                using (MemoryStream ms = new MemoryStream())
                {
                    int uPos = 0;
                    bool wroteName = false, wroteEmail = false, wrotePic = false;
                    while (uPos < uProto.Length)
                    {
                        int fieldStart = uPos;
                        ulong uTag = ReadVarint(uProto, ref uPos);
                        int uFn = (int)(uTag >> 3);
                        int uWt = (int)(uTag & 7);
                        if (uWt == 0)
                        {
                            ReadVarint(uProto, ref uPos);
                            ms.Write(uProto, fieldStart, uPos - fieldStart);
                        }
                        else if (uWt == 2)
                        {
                            int uLen = (int)ReadVarint(uProto, ref uPos);
                            if (uLen < 0 || uPos + uLen > uProto.Length) break;
                            if (uFn == 3)
                            {
                                byte[] b = EncodeFieldString(3, !string.IsNullOrEmpty(displayName) ? displayName : (email ?? ""));
                                ms.Write(b, 0, b.Length);
                                wroteName = true;
                            }
                            else if (uFn == 7)
                            {
                                byte[] b = EncodeFieldString(7, email ?? "");
                                ms.Write(b, 0, b.Length);
                                wroteEmail = true;
                            }
                            else if (uFn == 38)
                            {
                                if (!string.IsNullOrEmpty(pictureUrl))
                                {
                                    byte[] b = EncodeFieldString(38, pictureUrl);
                                    ms.Write(b, 0, b.Length);
                                }
                                wrotePic = true;
                            }
                            else
                            {
                                ms.Write(uProto, fieldStart, (uPos + uLen) - fieldStart);
                            }
                            uPos += uLen;
                        }
                        else break;
                    }
                    if (!wroteName && !string.IsNullOrEmpty(displayName))
                    {
                        byte[] b = EncodeFieldString(3, displayName);
                        ms.Write(b, 0, b.Length);
                    }
                    if (!wroteEmail && !string.IsNullOrEmpty(email))
                    {
                        byte[] b = EncodeFieldString(7, email);
                        ms.Write(b, 0, b.Length);
                    }
                    if (!wrotePic && !string.IsNullOrEmpty(pictureUrl))
                    {
                        byte[] b = EncodeFieldString(38, pictureUrl);
                        ms.Write(b, 0, b.Length);
                    }

                    string newInnerB64 = Convert.ToBase64String(ms.ToArray());
                    byte[] entry = ConcatBytes(
                        EncodeFieldString(1, "userStatusSentinelKey"),
                        EncodeFieldBytes(2, EncodeFieldString(1, newInnerB64))
                    );
                    byte[] newOuter = EncodeFieldBytes(1, entry);
                    return Convert.ToBase64String(newOuter);
                }
            }
            catch
            {
                return null;
            }
        }
    }

    // =========================================================================
    // OAuth client discovery
    // The Google OAuth client used by Antigravity is not stored in this repo:
    // it is read from the installed language_server binaries at runtime.
    // A binary holds several client IDs/secrets, so the caller confirms the
    // right pair with Google (see SwitcherCore.ExchangeRefreshTokenForCredentialJson).
    // =========================================================================
    internal static class OAuthClientLocator
    {
        private static readonly Regex ClientIdRx = new Regex(@"[0-9]{6,15}-[a-z0-9]{32}\.apps\.googleusercontent\.com", RegexOptions.Compiled);
        private static readonly Regex SecretRx = new Regex(@"GOCSPX-[A-Za-z0-9_-]{28}", RegexOptions.Compiled);

        // language_server binaries shipped with Antigravity 2.0 / Antigravity IDE
        public static List<string> FindBinaries(string agy2ExePath, string ideExePath)
        {
            List<string> files = new List<string>();
            AddIfExists(files, ideExePath, @"resources\app\extensions\antigravity\bin\language_server_windows_x64.exe");
            AddIfExists(files, agy2ExePath, @"resources\bin\language_server.exe");

            // Fallback when the layout changes: any language_server*.exe under resources\
            if (files.Count == 0)
            {
                foreach (string exe in new string[] { ideExePath, agy2ExePath })
                {
                    if (string.IsNullOrEmpty(exe)) continue;
                    try
                    {
                        string res = Path.Combine(Path.GetDirectoryName(exe), "resources");
                        if (!Directory.Exists(res)) continue;
                        foreach (string f in Directory.GetFiles(res, "language_server*.exe", SearchOption.AllDirectories))
                        {
                            if (!files.Contains(f)) files.Add(f);
                        }
                    }
                    catch (Exception) { }
                }
            }
            return files;
        }

        private static void AddIfExists(List<string> files, string exePath, string relative)
        {
            if (string.IsNullOrEmpty(exePath)) return;
            try
            {
                string f = Path.Combine(Path.GetDirectoryName(exePath), relative);
                if (File.Exists(f) && !files.Contains(f)) files.Add(f);
            }
            catch (ArgumentException) { }
        }

        // Scans the files in 4 MB chunks (with overlap, so a match is never split) and collects distinct IDs/secrets
        public static void ScanForCandidates(List<string> files, List<string> clientIds, List<string> secrets)
        {
            const int chunk = 4 * 1024 * 1024, overlap = 256;
            Encoding latin1 = Encoding.GetEncoding(28591);
            foreach (string file in files)
            {
                using (FileStream fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                {
                    byte[] buf = new byte[chunk + overlap];
                    int carry = 0, read;
                    while ((read = fs.Read(buf, carry, chunk)) > 0)
                    {
                        int len = carry + read;
                        string text = latin1.GetString(buf, 0, len);
                        foreach (Match m in ClientIdRx.Matches(text)) if (!clientIds.Contains(m.Value)) clientIds.Add(m.Value);
                        foreach (Match m in SecretRx.Matches(text)) if (!secrets.Contains(m.Value)) secrets.Add(m.Value);
                        carry = Math.Min(overlap, len);
                        Buffer.BlockCopy(buf, len - carry, buf, 0, carry);
                    }
                }
            }
        }
    }

    // =========================================================================
    // Data Models
    // =========================================================================
    public class AccountProfile
    {
        public int SlotId { get; set; }
        public string Email { get; set; }
        public string DisplayName { get; set; }
        public string Note { get; set; }
        public string GoogleSub { get; set; }
        public string AuthMethod { get; set; }
        public string TokenExpiry { get; set; }
        public string SavedAt { get; set; }
        public string SavedFromMachine { get; set; }
        public string SavedAntigravityVersion { get; set; }
        public string CredentialJson { get; set; }
        public string LastLoginIsGcpTos { get; set; }

        // Google profile picture URL from the id_token (runtime only, not saved)
        [ScriptIgnore]
        public string PictureUrl { get; set; }
    }

    public class AppSettings
    {
        public string CustomAntigravityExePath { get; set; }
        public string CustomAntigravityIdeExePath { get; set; }
        public bool ApplyToIde { get; set; }
        public bool ConfirmBeforeClosingApp { get; set; }
        public bool AutoRelaunchAfterSwitch { get; set; }
        public bool MinimizeToTrayOnClose { get; set; }
        public string UiThemeMode { get; set; } // "System" | "Light" | "Dark"

        // OAuth client of the installed Antigravity, found by OAuthClientLocator and confirmed by Google (local cache only)
        public string OAuthClientId { get; set; }
        public string OAuthClientSecret { get; set; }

        public AppSettings()
        {
            CustomAntigravityExePath = "";
            CustomAntigravityIdeExePath = "";
            ApplyToIde = false;
            ConfirmBeforeClosingApp = true;
            AutoRelaunchAfterSwitch = true;
            MinimizeToTrayOnClose = true;
            UiThemeMode = "System";
            OAuthClientId = "";
            OAuthClientSecret = "";
        }
    }

    public class EnvironmentStatus
    {
        public bool IsInstalled { get; set; }
        public string ExePath { get; set; }
        public string DetectionSource { get; set; }
        public string Version { get; set; }
        public bool IsRunning { get; set; }
        public List<int> AntigravityPids { get; set; }
        public List<int> LanguageServerPids { get; set; }
        public string AppDataRoamingDir { get; set; }
        public string AppStorageJsonPath { get; set; }
        public bool AppStorageJsonExists { get; set; }
        public string GeminiAntigravityDir { get; set; }
        public string StatePbtxtPath { get; set; }
        public bool StatePbtxtExists { get; set; }
        public string LockfilePath { get; set; }
        public bool LockfileExists { get; set; }

        // Antigravity IDE status
        public bool IdeIsInstalled { get; set; }
        public string IdeExePath { get; set; }
        public string IdeDetectionSource { get; set; }
        public string IdeVersion { get; set; }
        public bool IdeIsRunning { get; set; }
        public List<int> IdePids { get; set; }
        public List<int> IdeLanguageServerPids { get; set; }
        public string IdeAppDataRoamingDir { get; set; }
        public string IdeStateVscdbPath { get; set; }
        public bool IdeStateVscdbExists { get; set; }

        public EnvironmentStatus()
        {
            AntigravityPids = new List<int>();
            LanguageServerPids = new List<int>();
            IdePids = new List<int>();
            IdeLanguageServerPids = new List<int>();
        }
    }

    // =========================================================================
    // Core Switcher Logic
    // =========================================================================
    public class SwitcherCore
    {
        public string BaseDir { get; private set; }
        public string ProfilesDir { get; private set; }
        public string AccountsFilePath { get; private set; }
        public string SettingsPath { get; private set; }
        public string RollbackBackupPath { get; private set; }
        public AppSettings Settings { get; private set; }
        public List<AccountProfile> Accounts { get; private set; }
        public List<string> ActivityLogs { get; private set; }
        public string LastErrorCode { get; private set; }
        public string LastErrorDetail { get; private set; }

        private static readonly Encoding Utf8NoBom = new UTF8Encoding(false);
        private JavaScriptSerializer _json;

        public SwitcherCore()
        {
            _json = new JavaScriptSerializer();
            _json.MaxJsonLength = 20 * 1024 * 1024;
            string asmLoc = typeof(SwitcherCore).Assembly.Location;
            BaseDir = !string.IsNullOrEmpty(asmLoc) ? Path.GetDirectoryName(asmLoc) : AppDomain.CurrentDomain.BaseDirectory;
            ProfilesDir = Path.Combine(BaseDir, "profiles");
            AccountsFilePath = Path.Combine(ProfilesDir, "accounts.json");
            SettingsPath = Path.Combine(ProfilesDir, "switcher_settings.json");
            RollbackBackupPath = Path.Combine(ProfilesDir, "last_rollback_backup.json");
            ActivityLogs = new List<string>();
            Accounts = new List<AccountProfile>();
            LastErrorCode = "NONE";
            LastErrorDetail = "Chưa có lỗi nào ghi nhận.";

            if (!Directory.Exists(ProfilesDir))
            {
                Directory.CreateDirectory(ProfilesDir);
            }
            LoadSettings();
            LoadAccounts();
            Log(string.Format("Khởi tạo thành công. Đã nạp {0} tài khoản trong danh sách.", Accounts.Count));
        }

        // Log can be called from background work (saving, OAuth discovery), so the list is locked
        public void Log(string message)
        {
            string line = string.Format("[{0:yyyy-MM-dd HH:mm:ss}] {1}", DateTime.Now, message);
            lock (ActivityLogs)
            {
                ActivityLogs.Add(line);
                if (ActivityLogs.Count > 250)
                {
                    ActivityLogs.RemoveAt(0);
                }
            }
        }

        public string[] GetActivityLogSnapshot()
        {
            lock (ActivityLogs)
            {
                return ActivityLogs.ToArray();
            }
        }

        public void RecordError(string code, string detail)
        {
            LastErrorCode = code;
            LastErrorDetail = detail;
            Log(string.Format("❌ LỖI [{0}]: {1}", code, detail));
            WriteErrorLog(code, detail, null);
        }

        // Same as RecordError, but the log file also gets the exception's stack trace
        public void RecordException(string code, Exception ex)
        {
            LastErrorCode = code;
            LastErrorDetail = ex.Message;
            Log(string.Format("❌ LỖI [{0}]: {1}", code, ex.Message));
            WriteErrorLog(code, ex.Message, ex);
        }

        // ---------------------------------------------------------------------
        // Error log files: one file per error in <app folder>\logs\Error_yyyy-MM-dd_HH-mm-ss.txt
        // ---------------------------------------------------------------------
        public string ErrorLogDir
        {
            get { return Path.Combine(BaseDir, "logs"); }
        }

        // Returns the written file, or null when the log folder is not writable
        public string WriteErrorLog(string code, string detail, Exception ex)
        {
            try
            {
                Directory.CreateDirectory(ErrorLogDir);
                DateTime now = DateTime.Now;
                string baseName = "Error_" + now.ToString("yyyy-MM-dd_HH-mm-ss");
                string path = Path.Combine(ErrorLogDir, baseName + ".txt");
                for (int n = 2; File.Exists(path); n++)
                {
                    path = Path.Combine(ErrorLogDir, baseName + "_" + n + ".txt");
                }

                StringBuilder sb = new StringBuilder();
                sb.AppendLine("ANTIGRAVITY ACCOUNT SWITCHER — LOG LỖI");
                sb.AppendFormat("Thời gian : {0:yyyy-MM-dd HH:mm:ss.fff}\r\n", now);
                sb.AppendFormat("Mã lỗi    : {0}\r\n", code);
                sb.AppendFormat("Chi tiết  : {0}\r\n", detail);
                sb.AppendFormat("Phiên bản : {0}\r\n", typeof(SwitcherCore).Assembly.GetName().Version);
                sb.AppendFormat("Hệ điều hành: {0} (64-bit: {1}), .NET {2}\r\n", Environment.OSVersion, Environment.Is64BitOperatingSystem, Environment.Version);
                if (ex != null)
                {
                    sb.AppendLine();
                    sb.AppendLine("[Exception]");
                    sb.AppendLine(ex.ToString());
                }
                sb.AppendLine();
                sb.AppendLine("[Nhật ký hoạt động gần nhất]");
                string[] logs = GetActivityLogSnapshot();
                for (int i = Math.Max(0, logs.Length - 40); i < logs.Length; i++)
                {
                    sb.AppendLine("  " + logs[i]);
                }

                File.WriteAllText(path, sb.ToString(), Utf8NoBom);
                return path;
            }
            catch (IOException) { return null; }
            catch (UnauthorizedAccessException) { return null; }
        }

        public int CountErrorLogs()
        {
            try
            {
                return GetErrorLogFiles().Count;
            }
            catch (IOException) { return 0; }
            catch (UnauthorizedAccessException) { return 0; }
        }

        // Error_*.txt, plus Loi_*.txt written by earlier test builds so they can be cleaned up too
        private List<string> GetErrorLogFiles()
        {
            List<string> files = new List<string>();
            if (!Directory.Exists(ErrorLogDir)) return files;
            files.AddRange(Directory.GetFiles(ErrorLogDir, "Error_*.txt"));
            files.AddRange(Directory.GetFiles(ErrorLogDir, "Loi_*.txt"));
            return files;
        }

        // Deletes every error log file; returns how many were removed
        public int ClearErrorLogs()
        {
            int removed = 0;
            if (!Directory.Exists(ErrorLogDir)) return 0;
            foreach (string f in GetErrorLogFiles())
            {
                try
                {
                    File.Delete(f);
                    removed++;
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
            Log(string.Format("Đã xóa {0} file log lỗi.", removed));
            return removed;
        }

        public void LoadSettings()
        {
            try
            {
                if (File.Exists(SettingsPath))
                {
                    string content = File.ReadAllText(SettingsPath, Encoding.UTF8);
                    Settings = _json.Deserialize<AppSettings>(content) ?? new AppSettings();
                }
                else
                {
                    Settings = new AppSettings();
                }
            }
            catch (Exception ex)
            {
                Settings = new AppSettings();
                string backup = BackupUnreadableFile(SettingsPath);
                RecordError("ERR_LOAD_SETTINGS", "Không đọc được file cài đặt, dùng cài đặt mặc định: " + ex.Message +
                    (backup != null ? (" — Bản sao file lỗi: " + backup) : ""));
            }
        }

        public void SaveSettings()
        {
            try
            {
                if (!Directory.Exists(ProfilesDir))
                {
                    Directory.CreateDirectory(ProfilesDir);
                }
                string json = _json.Serialize(Settings);
                File.WriteAllText(SettingsPath, json, Utf8NoBom);
            }
            catch (Exception ex)
            {
                RecordError("ERR_SAVE_SETTINGS", ex.Message);
            }
        }

        public void LoadAccounts()
        {
            Accounts = new List<AccountProfile>();
            try
            {
                if (File.Exists(AccountsFilePath))
                {
                    string raw = File.ReadAllText(AccountsFilePath, Encoding.UTF8);
                    if (!string.IsNullOrEmpty(raw.Trim()))
                    {
                        List<AccountProfile> loaded = _json.Deserialize<List<AccountProfile>>(raw);
                        if (loaded != null)
                        {
                            Accounts = loaded;
                            // Clear the old auto-generated notes ("Tài khoản Ultra #N"): the plan can't be read locally
                            foreach (AccountProfile a in Accounts)
                            {
                                if (a.Note != null && Regex.IsMatch(a.Note, @"^Tài khoản Ultra #\d+$")) a.Note = "";
                            }
                        }
                    }
                }

                // Auto-migrate legacy slot_1.json / slot_2.json if not already in Accounts list
                bool migratedAny = false;
                for (int s = 1; s <= 5; s++)
                {
                    string legacyPath = Path.Combine(ProfilesDir, string.Format("slot_{0}.json", s));
                    if (File.Exists(legacyPath))
                    {
                        try
                        {
                            string legacyRaw = File.ReadAllText(legacyPath, Encoding.UTF8);
                            AccountProfile legacyProf = _json.Deserialize<AccountProfile>(legacyRaw);
                            if (legacyProf != null && !string.IsNullOrEmpty(legacyProf.CredentialJson))
                            {
                                if (FindAccountIndexByEmailOrSub(legacyProf.Email, legacyProf.GoogleSub) < 0)
                                {
                                    Accounts.Add(legacyProf);
                                    migratedAny = true;
                                }
                            }
                        }
                        catch (Exception legacyEx)
                        {
                            Log(string.Format("Bỏ qua file slot cũ '{0}' (không đọc được): {1}", Path.GetFileName(legacyPath), legacyEx.Message));
                        }
                    }
                }

                ReindexAccounts();
                if (migratedAny)
                {
                    SaveAccounts();
                    Log("Đã tự động đồng bộ dữ liệu từ file slot cũ sang danh sách accounts.json.");
                }
            }
            catch (Exception ex)
            {
                // The next save would overwrite the unreadable file with an empty list: keep a copy first
                string backup = BackupUnreadableFile(AccountsFilePath);
                RecordError("ERR_LOAD_ACCOUNTS", ex.Message +
                    (backup != null ? (" — Đã giữ lại bản sao file lỗi: " + backup) : ""));
            }
        }

        // Copies a file that could not be parsed to "<name>.unreadable-<time>.json" so no data is lost; returns the copy's path
        private static string BackupUnreadableFile(string path)
        {
            try
            {
                if (!File.Exists(path)) return null;
                string copy = Path.Combine(Path.GetDirectoryName(path),
                    Path.GetFileNameWithoutExtension(path) + ".unreadable-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".json");
                File.Copy(path, copy, true);
                return copy;
            }
            catch (IOException) { return null; }
            catch (UnauthorizedAccessException) { return null; }
        }

        public void SaveAccounts()
        {
            try
            {
                if (!Directory.Exists(ProfilesDir))
                {
                    Directory.CreateDirectory(ProfilesDir);
                }
                ReindexAccounts();
                string json = _json.Serialize(Accounts);
                File.WriteAllText(AccountsFilePath, json, Utf8NoBom);
            }
            catch (Exception ex)
            {
                RecordError("ERR_SAVE_ACCOUNTS", ex.Message);
            }
        }

        private void ReindexAccounts()
        {
            for (int i = 0; i < Accounts.Count; i++)
            {
                Accounts[i].SlotId = i + 1;
            }
        }

        public int FindAccountIndexByEmailOrSub(string email, string sub)
        {
            for (int i = 0; i < Accounts.Count; i++)
            {
                AccountProfile a = Accounts[i];
                if (!string.IsNullOrEmpty(email) && !string.IsNullOrEmpty(a.Email) &&
                    string.Equals(a.Email.Trim(), email.Trim(), StringComparison.OrdinalIgnoreCase))
                {
                    return i;
                }
                if (!string.IsNullOrEmpty(sub) && !string.IsNullOrEmpty(a.GoogleSub) &&
                    string.Equals(a.GoogleSub.Trim(), sub.Trim(), StringComparison.Ordinal))
                {
                    return i;
                }
            }
            return -1;
        }

        public static string GetDefaultAntigravityInstallDir()
        {
            string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            return Path.Combine(Path.Combine(localAppData, "Programs"), "Antigravity");
        }

        public static string GetDefaultAntigravityExePath()
        {
            return Path.Combine(GetDefaultAntigravityInstallDir(), "Antigravity.exe");
        }

        public static string GetDefaultAntigravityIdeInstallDir()
        {
            string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            return Path.Combine(Path.Combine(localAppData, "Programs"), "Antigravity IDE");
        }

        public static string GetDefaultAntigravityIdeExePath()
        {
            return Path.Combine(GetDefaultAntigravityIdeInstallDir(), "Antigravity IDE.exe");
        }

        private string _cachedAgyExePath;
        private string _cachedAgyVersion;
        private string _cachedIdeExePath;
        private string _cachedIdeVersion;

        public EnvironmentStatus InspectEnvironment()
        {
            EnvironmentStatus status = new EnvironmentStatus();

            string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            string userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

            status.AppDataRoamingDir = Path.Combine(appData, "Antigravity");
            status.AppStorageJsonPath = Path.Combine(status.AppDataRoamingDir, "app_storage.json");
            status.AppStorageJsonExists = File.Exists(status.AppStorageJsonPath);
            status.LockfilePath = Path.Combine(status.AppDataRoamingDir, "lockfile");
            status.LockfileExists = File.Exists(status.LockfilePath);

            status.GeminiAntigravityDir = Path.Combine(Path.Combine(userProfile, ".gemini"), "antigravity");
            status.StatePbtxtPath = Path.Combine(status.GeminiAntigravityDir, "antigravity_state.pbtxt");
            status.StatePbtxtExists = File.Exists(status.StatePbtxtPath);

            status.IdeAppDataRoamingDir = Path.Combine(appData, "Antigravity IDE");
            status.IdeStateVscdbPath = Path.Combine(Path.Combine(Path.Combine(status.IdeAppDataRoamingDir, "User"), "globalStorage"), "state.vscdb");
            status.IdeStateVscdbExists = File.Exists(status.IdeStateVscdbPath);

            // Detect Antigravity.exe path first (avoid slow cross-process MainModule lookup if custom or default path exists)
            string defaultPath = GetDefaultAntigravityExePath();
            if (!string.IsNullOrEmpty(Settings.CustomAntigravityExePath) && File.Exists(Settings.CustomAntigravityExePath))
            {
                status.ExePath = Settings.CustomAntigravityExePath;
                status.DetectionSource = "Đường dẫn tùy chỉnh (User Custom)";
            }
            else if (File.Exists(defaultPath))
            {
                status.ExePath = defaultPath;
                status.DetectionSource = "Mặc định (%LOCALAPPDATA%\\Programs\\Antigravity)";
            }

            // Detect running processes (Antigravity 2.0)
            string runningExePath = null;
            try
            {
                foreach (Process p in Process.GetProcessesByName("Antigravity"))
                {
                    status.AntigravityPids.Add(p.Id);
                    if (string.IsNullOrEmpty(status.ExePath) && runningExePath == null)
                    {
                        try { runningExePath = p.MainModule.FileName; } catch { }
                    }
                }
            }
            catch { }

            try
            {
                foreach (Process p in Process.GetProcessesByName("language_server"))
                {
                    status.LanguageServerPids.Add(p.Id);
                }
            }
            catch { }

            status.IsRunning = (status.AntigravityPids.Count > 0 || status.LanguageServerPids.Count > 0);

            if (string.IsNullOrEmpty(status.ExePath) && !string.IsNullOrEmpty(runningExePath) && File.Exists(runningExePath))
            {
                status.ExePath = runningExePath;
                status.DetectionSource = "Tiến trình đang chạy (Active Process)";
            }

            if (string.IsNullOrEmpty(status.ExePath))
            {
                try
                {
                    using (RegistryKey key = Registry.CurrentUser.OpenSubKey(@"Software\Classes\antigravity\shell\open\command"))
                    {
                        if (key != null)
                        {
                            string cmd = key.GetValue("") as string;
                            if (!string.IsNullOrEmpty(cmd))
                            {
                                Match m = Regex.Match(cmd, "\"([^\"]+Antigravity\\.exe)\"", RegexOptions.IgnoreCase);
                                if (m.Success && File.Exists(m.Groups[1].Value))
                                {
                                    status.ExePath = m.Groups[1].Value;
                                    status.DetectionSource = "Registry (Protocol Handler)";
                                }
                            }
                        }
                    }
                }
                catch { }
            }

            if (string.IsNullOrEmpty(status.ExePath))
            {
                string pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
                string pfPath = Path.Combine(Path.Combine(pf, "Antigravity"), "Antigravity.exe");
                if (File.Exists(pfPath))
                {
                    status.ExePath = pfPath;
                    status.DetectionSource = "Program Files";
                }
            }

            status.IsInstalled = !string.IsNullOrEmpty(status.ExePath) && File.Exists(status.ExePath);

            status.Version = "Chưa xác định";
            if (status.IsInstalled)
            {
                if (string.Equals(_cachedAgyExePath, status.ExePath, StringComparison.OrdinalIgnoreCase) && !string.IsNullOrEmpty(_cachedAgyVersion))
                {
                    status.Version = _cachedAgyVersion;
                }
                else
                {
                    try
                    {
                        string installDir = Path.GetDirectoryName(status.ExePath);
                        string versionFile = Path.Combine(installDir, "version");
                        string vFileStr = null;
                        if (File.Exists(versionFile))
                        {
                            vFileStr = File.ReadAllText(versionFile, Encoding.UTF8).Trim();
                        }

                        string prodVer = null;
                        try
                        {
                            FileVersionInfo fvi = FileVersionInfo.GetVersionInfo(status.ExePath);
                            prodVer = fvi.ProductVersion;
                        }
                        catch { }

                        if (!string.IsNullOrEmpty(vFileStr) && !string.IsNullOrEmpty(prodVer) && vFileStr != prodVer)
                        {
                            status.Version = string.Format("{0} (Engine: {1})", vFileStr, prodVer);
                        }
                        else if (!string.IsNullOrEmpty(vFileStr))
                        {
                            status.Version = vFileStr;
                        }
                        else if (!string.IsNullOrEmpty(prodVer))
                        {
                            status.Version = prodVer;
                        }
                        _cachedAgyExePath = status.ExePath;
                        _cachedAgyVersion = status.Version;
                    }
                    catch { }
                }
            }

            // Detect Antigravity IDE path & running processes
            string defaultIdePath = GetDefaultAntigravityIdeExePath();
            if (!string.IsNullOrEmpty(Settings.CustomAntigravityIdeExePath) && File.Exists(Settings.CustomAntigravityIdeExePath))
            {
                status.IdeExePath = Settings.CustomAntigravityIdeExePath;
                status.IdeDetectionSource = "Đường dẫn tùy chỉnh (User Custom)";
            }
            else if (File.Exists(defaultIdePath))
            {
                status.IdeExePath = defaultIdePath;
                status.IdeDetectionSource = "Mặc định (%LOCALAPPDATA%\\Programs\\Antigravity IDE)";
            }

            string runningIdeExePath = null;
            try
            {
                foreach (Process p in Process.GetProcessesByName("Antigravity IDE"))
                {
                    status.IdePids.Add(p.Id);
                    if (string.IsNullOrEmpty(status.IdeExePath) && runningIdeExePath == null)
                    {
                        try { runningIdeExePath = p.MainModule.FileName; } catch { }
                    }
                }
            }
            catch { }

            try
            {
                foreach (Process p in Process.GetProcessesByName("language_server_windows_x64"))
                {
                    status.IdeLanguageServerPids.Add(p.Id);
                }
            }
            catch { }

            status.IdeIsRunning = (status.IdePids.Count > 0 || status.IdeLanguageServerPids.Count > 0);

            if (string.IsNullOrEmpty(status.IdeExePath) && !string.IsNullOrEmpty(runningIdeExePath) && File.Exists(runningIdeExePath))
            {
                status.IdeExePath = runningIdeExePath;
                status.IdeDetectionSource = "Tiến trình đang chạy (Active Process)";
            }

            if (string.IsNullOrEmpty(status.IdeExePath))
            {
                string pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
                string pfIdePath = Path.Combine(Path.Combine(pf, "Antigravity IDE"), "Antigravity IDE.exe");
                if (File.Exists(pfIdePath))
                {
                    status.IdeExePath = pfIdePath;
                    status.IdeDetectionSource = "Program Files";
                }
            }

            status.IdeIsInstalled = !string.IsNullOrEmpty(status.IdeExePath) && File.Exists(status.IdeExePath);
            status.IdeVersion = "Chưa xác định";
            if (status.IdeIsInstalled)
            {
                if (string.Equals(_cachedIdeExePath, status.IdeExePath, StringComparison.OrdinalIgnoreCase) && !string.IsNullOrEmpty(_cachedIdeVersion))
                {
                    status.IdeVersion = _cachedIdeVersion;
                }
                else
                {
                    try
                    {
                        FileVersionInfo fvi = FileVersionInfo.GetVersionInfo(status.IdeExePath);
                        if (!string.IsNullOrEmpty(fvi.ProductVersion))
                        {
                            status.IdeVersion = fvi.ProductVersion.Trim();
                        }
                        _cachedIdeExePath = status.IdeExePath;
                        _cachedIdeVersion = status.IdeVersion;
                    }
                    catch { }
                }
            }

            return status;
        }

        public AccountProfile ReadCurrentActiveAccount(out string errorMsg)
        {
            return ReadCurrentActiveAccount(null, out errorMsg);
        }

        public AccountProfile ReadCurrentActiveAccount(EnvironmentStatus env, out string errorMsg)
        {
            errorMsg = null;
            int win32Err;
            string credJson = NativeCred.ReadAntigravityCredential(out win32Err);
            if (string.IsNullOrEmpty(credJson))
            {
                if (win32Err == 1168)
                {
                    errorMsg = "Chưa có tài khoản nào đăng nhập trong Credential Manager (gemini:antigravity).";
                }
                else
                {
                    errorMsg = string.Format("Không đọc được Credential gemini:antigravity (Win32 Error: {0}).", win32Err);
                }
                return null;
            }

            return ParseAccountFromCredentialJson(credJson, 0, env, out errorMsg);
        }

        public AccountProfile ParseAccountFromCredentialJson(string credJson, int slotId, out string errorMsg)
        {
            return ParseAccountFromCredentialJson(credJson, slotId, null, out errorMsg);
        }

        public AccountProfile ParseAccountFromCredentialJson(string credJson, int slotId, EnvironmentStatus env, out string errorMsg)
        {
            errorMsg = null;
            try
            {
                Dictionary<string, object> root = _json.Deserialize<Dictionary<string, object>>(credJson);
                if (root == null || !root.ContainsKey("token"))
                {
                    errorMsg = "Cấu trúc JSON Credential không hợp lệ (thiếu trường 'token').";
                    return null;
                }

                AccountProfile profile = new AccountProfile();
                profile.SlotId = slotId;
                profile.CredentialJson = credJson;
                profile.AuthMethod = root.ContainsKey("auth_method") ? Convert.ToString(root["auth_method"]) : "consumer";

                Dictionary<string, object> tokenObj = root["token"] as Dictionary<string, object>;
                if (tokenObj != null && tokenObj.ContainsKey("expiry"))
                {
                    profile.TokenExpiry = Convert.ToString(tokenObj["expiry"]);
                }

                if (root.ContainsKey("id_token") && root["id_token"] != null)
                {
                    string idToken = Convert.ToString(root["id_token"]);
                    Dictionary<string, object> claims = DecodeJwtPayload(idToken);
                    if (claims != null)
                    {
                        if (claims.ContainsKey("email")) profile.Email = Convert.ToString(claims["email"]);
                        if (claims.ContainsKey("name")) profile.DisplayName = Convert.ToString(claims["name"]);
                        if (claims.ContainsKey("sub")) profile.GoogleSub = Convert.ToString(claims["sub"]);
                        profile.PictureUrl = claims.ContainsKey("picture") ? Convert.ToString(claims["picture"]) : "";
                    }
                }

                if (env == null) env = InspectEnvironment();
                if (env.AppStorageJsonExists)
                {
                    try
                    {
                        string appStorageRaw = File.ReadAllText(env.AppStorageJsonPath, Encoding.UTF8);
                        Dictionary<string, object> appStorage = _json.Deserialize<Dictionary<string, object>>(appStorageRaw);
                        if (appStorage != null)
                        {
                            if (string.IsNullOrEmpty(profile.Email) && appStorage.ContainsKey("jetski.onboarding.lastLoginUsername"))
                            {
                                profile.Email = Convert.ToString(appStorage["jetski.onboarding.lastLoginUsername"]);
                            }
                            if (appStorage.ContainsKey("jetski.onboarding.lastLoginIsGcpTos"))
                            {
                                profile.LastLoginIsGcpTos = Convert.ToString(appStorage["jetski.onboarding.lastLoginIsGcpTos"]);
                            }
                        }
                    }
                    catch { }
                }

                if (string.IsNullOrEmpty(profile.Email))
                {
                    profile.Email = "(Tài khoản đã xác thực - Không rõ Email)";
                }
                if (string.IsNullOrEmpty(profile.DisplayName))
                {
                    profile.DisplayName = profile.Email;
                }
                if (string.IsNullOrEmpty(profile.LastLoginIsGcpTos))
                {
                    profile.LastLoginIsGcpTos = "false";
                }

                return profile;
            }
            catch (Exception ex)
            {
                errorMsg = "Lỗi phân tích dữ liệu Credential: " + ex.Message;
                return null;
            }
        }

        // Picture URL of a saved account (read once from its stored id_token)
        public string GetPictureUrl(AccountProfile acc)
        {
            if (acc == null) return "";
            if (acc.PictureUrl == null)
            {
                acc.PictureUrl = "";
                try
                {
                    Dictionary<string, object> root = string.IsNullOrEmpty(acc.CredentialJson) ? null : _json.Deserialize<Dictionary<string, object>>(acc.CredentialJson);
                    if (root != null && root.ContainsKey("id_token") && root["id_token"] != null)
                    {
                        Dictionary<string, object> claims = DecodeJwtPayload(Convert.ToString(root["id_token"]));
                        if (claims != null && claims.ContainsKey("picture")) acc.PictureUrl = Convert.ToString(claims["picture"]);
                    }
                }
                catch { }
            }
            return acc.PictureUrl;
        }

        private Dictionary<string, object> DecodeJwtPayload(string jwt)
        {
            try
            {
                string[] parts = jwt.Split('.');
                if (parts.Length < 2) return null;
                string payload = parts[1].Replace('-', '+').Replace('_', '/');
                switch (payload.Length % 4)
                {
                    case 2: payload += "=="; break;
                    case 3: payload += "="; break;
                }
                byte[] bytes = Convert.FromBase64String(payload);
                string json = Encoding.UTF8.GetString(bytes);
                return _json.Deserialize<Dictionary<string, object>>(json);
            }
            catch
            {
                return null;
            }
        }

        public AccountProfile ReadCurrentIdeAccount(out string errorMsg, out string refreshTokenOut, out string accessTokenOut, out long expirySecOut)
        {
            return ReadCurrentIdeAccount(null, out errorMsg, out refreshTokenOut, out accessTokenOut, out expirySecOut);
        }

        public AccountProfile ReadCurrentIdeAccount(EnvironmentStatus env, out string errorMsg, out string refreshTokenOut, out string accessTokenOut, out long expirySecOut)
        {
            errorMsg = null;
            refreshTokenOut = null;
            accessTokenOut = null;
            expirySecOut = 0;

            if (env == null) env = InspectEnvironment();
            if (!env.IdeStateVscdbExists)
            {
                errorMsg = "Chưa tìm thấy file state.vscdb của Antigravity IDE.";
                return null;
            }

            string sqlErr;
            string rawOAuth = NativeSqlite.ReadItemValue(env.IdeStateVscdbPath, "antigravityUnifiedStateSync.oauthToken", out sqlErr);
            if (string.IsNullOrEmpty(rawOAuth))
            {
                errorMsg = !string.IsNullOrEmpty(sqlErr)
                    ? ("Không đọc được state.vscdb của Antigravity IDE: " + sqlErr)
                    : "Chưa có phiên đăng nhập nào trong Antigravity IDE.";
                return null;
            }

            string tokenType;
            if (!IdeTokenCodec.DecodeIdeOAuthTokenValue(rawOAuth, out accessTokenOut, out tokenType, out refreshTokenOut, out expirySecOut))
            {
                errorMsg = "Phiên trong Antigravity IDE đã đăng xuất hoặc không có OAuth token.";
                return null;
            }

            string email = null, displayName = null, picUrl = null;
            string rawUserStatus = NativeSqlite.ReadItemValue(env.IdeStateVscdbPath, "antigravityUnifiedStateSync.userStatus", out sqlErr);
            if (!string.IsNullOrEmpty(rawUserStatus))
            {
                IdeTokenCodec.DecodeIdeUserStatus(rawUserStatus, out email, out displayName, out picUrl);
            }

            if (string.IsNullOrEmpty(email) || string.IsNullOrEmpty(displayName))
            {
                string rawAuthStatus = NativeSqlite.ReadItemValue(env.IdeStateVscdbPath, "antigravityAuthStatus", out sqlErr);
                if (!string.IsNullOrEmpty(rawAuthStatus))
                {
                    try
                    {
                        Dictionary<string, object> authObj = _json.Deserialize<Dictionary<string, object>>(rawAuthStatus);
                        if (authObj != null)
                        {
                            if (string.IsNullOrEmpty(email) && authObj.ContainsKey("email")) email = Convert.ToString(authObj["email"]);
                            if (string.IsNullOrEmpty(displayName) && authObj.ContainsKey("name")) displayName = Convert.ToString(authObj["name"]);
                        }
                    }
                    catch { }
                }
            }

            if (string.IsNullOrEmpty(picUrl))
            {
                string rawPic = NativeSqlite.ReadItemValue(env.IdeStateVscdbPath, "antigravity.profileUrl", out sqlErr);
                if (!string.IsNullOrEmpty(rawPic)) picUrl = rawPic.Trim();
            }

            AccountProfile p = new AccountProfile();
            p.Email = !string.IsNullOrEmpty(email) ? email : "(Tài khoản IDE - Không rõ Email)";
            p.DisplayName = !string.IsNullOrEmpty(displayName) ? displayName : p.Email;
            p.PictureUrl = picUrl ?? "";
            p.AuthMethod = "consumer";
            p.LastLoginIsGcpTos = "false";
            if (expirySecOut > 0)
            {
                DateTime expUtc = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddSeconds(expirySecOut);
                p.TokenExpiry = expUtc.ToString("yyyy-MM-ddTHH:mm:ssZ");
            }
            int idx = FindAccountIndexByEmailOrSub(p.Email, null);
            if (idx >= 0)
            {
                p.GoogleSub = Accounts[idx].GoogleSub;
                if (string.IsNullOrEmpty(p.PictureUrl)) p.PictureUrl = GetPictureUrl(Accounts[idx]);
            }
            return p;
        }

        private bool ExchangeRefreshTokenForCredentialJson(
            string refreshToken,
            string accessTokenFallback,
            long expirySecFallback,
            string emailFallback,
            string nameFallback,
            string picUrlFallback,
            out string credJson,
            out string errorMsg)
        {
            credJson = null;
            errorMsg = null;
            if (string.IsNullOrEmpty(refreshToken))
            {
                errorMsg = "Refresh token từ Antigravity IDE bị trống.";
                return false;
            }

            string accessToken = accessTokenFallback ?? "";
            string tokenType = "Bearer";
            string expiryIso = (expirySecFallback > 0)
                ? new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddSeconds(expirySecFallback).ToString("yyyy-MM-ddTHH:mm:ss.fffZ")
                : DateTime.UtcNow.AddHours(1).ToString("yyyy-MM-ddTHH:mm:ss.fffZ");
            string idToken = null;

            string respJson, refreshErr;
            if (RefreshWithDiscoveredClient(refreshToken, out respJson, out refreshErr))
            {
                Dictionary<string, object> tokResp = _json.Deserialize<Dictionary<string, object>>(respJson);
                if (tokResp != null)
                {
                    if (tokResp.ContainsKey("access_token")) accessToken = Convert.ToString(tokResp["access_token"]);
                    if (tokResp.ContainsKey("token_type")) tokenType = Convert.ToString(tokResp["token_type"]);
                    if (tokResp.ContainsKey("id_token")) idToken = Convert.ToString(tokResp["id_token"]);
                    if (tokResp.ContainsKey("expires_in"))
                    {
                        int expIn = 3599;
                        int.TryParse(Convert.ToString(tokResp["expires_in"]), out expIn);
                        expiryIso = DateTime.UtcNow.AddSeconds(expIn).ToString("yyyy-MM-ddTHH:mm:ss.fffZ");
                    }
                }
            }
            else
            {
                Log("Cảnh báo khi lấy id_token từ Google OAuth (sẽ dùng thông tin từ IDE): " + refreshErr);
            }

            if (string.IsNullOrEmpty(idToken))
            {
                // Build synthetic JWT payload so ParseAccountFromCredentialJson can still extract email/name/picture offline
                Dictionary<string, object> claims = new Dictionary<string, object>();
                claims["email"] = emailFallback ?? "";
                claims["name"] = nameFallback ?? emailFallback ?? "";
                claims["picture"] = picUrlFallback ?? "";
                string claimsB64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(_json.Serialize(claims)))
                    .TrimEnd('=').Replace('+', '-').Replace('/', '_');
                idToken = "eyJhbGciOiJSUzI1NiIsInR5cCI6IkpXVCJ9." + claimsB64 + ".ide";
            }

            Dictionary<string, object> tokenObj = new Dictionary<string, object>();
            tokenObj["access_token"] = accessToken;
            tokenObj["token_type"] = tokenType;
            tokenObj["refresh_token"] = refreshToken;
            tokenObj["expiry"] = expiryIso;

            Dictionary<string, object> rootObj = new Dictionary<string, object>();
            rootObj["token"] = tokenObj;
            rootObj["auth_method"] = "consumer";
            rootObj["id_token"] = idToken;

            credJson = _json.Serialize(rootObj);
            return true;
        }

        // Tries the cached OAuth client first, then every ID/secret pair found in the installed binaries.
        // The first pair Google accepts is cached in settings, so the scan normally runs only once.
        private bool RefreshWithDiscoveredClient(string refreshToken, out string respJson, out string errorMsg)
        {
            respJson = null;
            errorMsg = null;
            bool rejected;
            string cachedId = Settings.OAuthClientId, cachedSecret = Settings.OAuthClientSecret;

            if (!string.IsNullOrEmpty(cachedId) && !string.IsNullOrEmpty(cachedSecret))
            {
                if (RequestTokenRefresh(cachedId, cachedSecret, refreshToken, out respJson, out rejected, out errorMsg)) return true;
                if (!rejected) return false;
            }

            List<string> ids, secrets;
            if (!GetOAuthClientCandidates(out ids, out secrets, out errorMsg)) return false;
            if (ids.Count == 0 || secrets.Count == 0)
            {
                errorMsg = "Không tìm thấy OAuth client trong file cài đặt của Antigravity (Antigravity đã đổi cách đóng gói?).";
                return false;
            }

            foreach (string id in ids)
            {
                foreach (string secret in secrets)
                {
                    if (id == cachedId && secret == cachedSecret) continue;
                    if (RequestTokenRefresh(id, secret, refreshToken, out respJson, out rejected, out errorMsg))
                    {
                        Settings.OAuthClientId = id;
                        Settings.OAuthClientSecret = secret;
                        SaveSettings();
                        return true;
                    }
                    // Anything other than "wrong client" (network, revoked token...) won't be fixed by another pair
                    if (!rejected) return false;
                }
            }
            errorMsg = "Google không chấp nhận cặp OAuth client nào tìm được cho refresh token này.";
            return false;
        }

        private readonly object _oauthScanLock = new object();
        private List<string> _oauthIds, _oauthSecrets;

        // Scans the installed binaries once per app run; a second caller waits for the running scan and reuses it
        private bool GetOAuthClientCandidates(out List<string> ids, out List<string> secrets, out string errorMsg)
        {
            errorMsg = null;
            lock (_oauthScanLock)
            {
                if (_oauthIds == null)
                {
                    List<string> foundIds = new List<string>(), foundSecrets = new List<string>();
                    try
                    {
                        EnvironmentStatus env = InspectEnvironment();
                        List<string> binaries = OAuthClientLocator.FindBinaries(env.ExePath, env.IdeExePath);
                        OAuthClientLocator.ScanForCandidates(binaries, foundIds, foundSecrets);
                        Log(string.Format("Dò OAuth client trong {0} file language_server: {1} client ID, {2} secret.", binaries.Count, foundIds.Count, foundSecrets.Count));
                    }
                    catch (Exception ex)
                    {
                        ids = null;
                        secrets = null;
                        errorMsg = "Không đọc được file language_server của Antigravity: " + ex.Message;
                        return false;
                    }
                    _oauthIds = foundIds;
                    _oauthSecrets = foundSecrets;
                }
                ids = _oauthIds;
                secrets = _oauthSecrets;
                return true;
            }
        }

        // Called in the background at startup so the first IDE save doesn't wait for the scan
        public void PrewarmOAuthClientDiscovery()
        {
            if (!Settings.ApplyToIde) return;
            if (!string.IsNullOrEmpty(Settings.OAuthClientId) && !string.IsNullOrEmpty(Settings.OAuthClientSecret)) return;
            List<string> ids, secrets;
            string err;
            if (!GetOAuthClientCandidates(out ids, out secrets, out err)) Log("Cảnh báo: " + err);
        }

        // One refresh_token grant. rejected = true when Google refuses the client itself (wrong ID/secret pair).
        private static bool RequestTokenRefresh(string clientId, string clientSecret, string refreshToken,
            out string respJson, out bool rejected, out string errorMsg)
        {
            respJson = null;
            rejected = false;
            errorMsg = null;
            try
            {
                ServicePointManager.SecurityProtocol |= (SecurityProtocolType)3072; // TLS 1.2
                HttpWebRequest req = (HttpWebRequest)WebRequest.Create("https://oauth2.googleapis.com/token");
                req.Method = "POST";
                req.ContentType = "application/x-www-form-urlencoded";
                req.Timeout = 8000;

                string postBody = string.Format(
                    "client_id={0}&client_secret={1}&refresh_token={2}&grant_type=refresh_token",
                    Uri.EscapeDataString(clientId),
                    Uri.EscapeDataString(clientSecret),
                    Uri.EscapeDataString(refreshToken));
                byte[] bodyBytes = Encoding.ASCII.GetBytes(postBody);
                req.ContentLength = bodyBytes.Length;
                using (Stream reqStream = req.GetRequestStream())
                {
                    reqStream.Write(bodyBytes, 0, bodyBytes.Length);
                }

                using (HttpWebResponse resp = (HttpWebResponse)req.GetResponse())
                using (StreamReader sr = new StreamReader(resp.GetResponseStream(), Encoding.UTF8))
                {
                    respJson = sr.ReadToEnd();
                    return true;
                }
            }
            catch (WebException ex)
            {
                string body = "";
                HttpWebResponse errResp = ex.Response as HttpWebResponse;
                if (errResp != null)
                {
                    try
                    {
                        using (StreamReader sr = new StreamReader(errResp.GetResponseStream(), Encoding.UTF8)) body = sr.ReadToEnd();
                    }
                    catch (IOException) { }
                }
                rejected = body.Contains("invalid_client") || body.Contains("unauthorized_client");
                errorMsg = ex.Message + (string.IsNullOrEmpty(body) ? "" : (" " + body.Trim()));
                return false;
            }
        }

        private static long ParseExpiryUnixSeconds(string expiryIso)
        {
            if (!string.IsNullOrEmpty(expiryIso))
            {
                DateTime dt;
                if (DateTime.TryParse(expiryIso, System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal, out dt))
                {
                    TimeSpan span = dt - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);
                    if (span.TotalSeconds > 0) return (long)span.TotalSeconds;
                }
            }
            return (long)(DateTime.UtcNow.AddHours(1) - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalSeconds;
        }

        public bool WriteAccountToIde(EnvironmentStatus env, AccountProfile targetProfile, out string errorMsg)
        {
            errorMsg = null;
            if (env == null) env = InspectEnvironment();
            if (!env.IdeStateVscdbExists)
            {
                errorMsg = "Chưa tìm thấy file dữ liệu state.vscdb của Antigravity IDE (Hãy mở Antigravity IDE ít nhất 1 lần trước).";
                return false;
            }

            try
            {
                Dictionary<string, object> root = _json.Deserialize<Dictionary<string, object>>(targetProfile.CredentialJson);
                Dictionary<string, object> tok = (root != null && root.ContainsKey("token")) ? (root["token"] as Dictionary<string, object>) : null;
                if (tok == null)
                {
                    errorMsg = "Dữ liệu token của tài khoản không hợp lệ.";
                    return false;
                }

                string accessToken = tok.ContainsKey("access_token") ? Convert.ToString(tok["access_token"]) : "";
                string tokenType = tok.ContainsKey("token_type") ? Convert.ToString(tok["token_type"]) : "Bearer";
                string refreshToken = tok.ContainsKey("refresh_token") ? Convert.ToString(tok["refresh_token"]) : "";
                string expiryStr = tok.ContainsKey("expiry") ? Convert.ToString(tok["expiry"]) : "";
                long expirySec = ParseExpiryUnixSeconds(expiryStr);

                string encodedOAuth = IdeTokenCodec.EncodeIdeOAuthTokenValue(accessToken, tokenType, refreshToken, expirySec);

                string sqlErr;
                string existingUserStatus = NativeSqlite.ReadItemValue(env.IdeStateVscdbPath, "antigravityUnifiedStateSync.userStatus", out sqlErr);
                string picUrl = GetPictureUrl(targetProfile);
                string patchedUserStatus = !string.IsNullOrEmpty(existingUserStatus)
                    ? IdeTokenCodec.PatchIdeUserStatus(existingUserStatus, targetProfile.Email, targetProfile.DisplayName, picUrl)
                    : null;

                Dictionary<string, object> authStatusObj = new Dictionary<string, object>();
                authStatusObj["name"] = !string.IsNullOrEmpty(targetProfile.DisplayName) ? targetProfile.DisplayName : (targetProfile.Email ?? "");
                authStatusObj["email"] = targetProfile.Email ?? "";
                authStatusObj["apiKey"] = accessToken;
                string authStatusJson = _json.Serialize(authStatusObj);

                Dictionary<string, string> kvToWrite = new Dictionary<string, string>();
                kvToWrite["antigravityUnifiedStateSync.oauthToken"] = encodedOAuth;
                kvToWrite["antigravityAuthStatus"] = authStatusJson;
                if (!string.IsNullOrEmpty(patchedUserStatus))
                {
                    kvToWrite["antigravityUnifiedStateSync.userStatus"] = patchedUserStatus;
                }
                if (!string.IsNullOrEmpty(picUrl))
                {
                    kvToWrite["antigravity.profileUrl"] = picUrl;
                }

                if (!NativeSqlite.WriteItemValues(env.IdeStateVscdbPath, kvToWrite, out sqlErr))
                {
                    errorMsg = "Không ghi được vào state.vscdb của Antigravity IDE: " + sqlErr;
                    return false;
                }

                Log("Đã đồng bộ OAuth Token & thông tin tài khoản vào state.vscdb của Antigravity IDE.");
                return true;
            }
            catch (Exception ex)
            {
                errorMsg = "Lỗi khi nạp phiên vào Antigravity IDE: " + ex.Message;
                return false;
            }
        }

        private int UpsertAccountInList(AccountProfile acc, string customNoteIfNew, out bool wasUpdated)
        {
            int existingIdx = FindAccountIndexByEmailOrSub(acc.Email, acc.GoogleSub);
            if (existingIdx >= 0)
            {
                wasUpdated = true;
                string oldNote = Accounts[existingIdx].Note;
                acc.Note = !string.IsNullOrEmpty(customNoteIfNew) ? customNoteIfNew : oldNote;
                if (string.IsNullOrEmpty(acc.GoogleSub)) acc.GoogleSub = Accounts[existingIdx].GoogleSub;
                Accounts[existingIdx] = acc;
                SaveAccounts();
                return existingIdx;
            }
            else
            {
                wasUpdated = false;
                acc.Note = customNoteIfNew ?? "";
                Accounts.Add(acc);
                SaveAccounts();
                return Accounts.Count - 1;
            }
        }

        public bool SaveOrUpdateCurrentAccount(string customNoteIfNew, out string message, out int savedIndex)
        {
            savedIndex = -1;
            EnvironmentStatus env = InspectEnvironment();
            List<string> results = new List<string>();

            // 1. Always read from Antigravity 2.0
            string errAgy2;
            AccountProfile currentAgy2 = ReadCurrentActiveAccount(out errAgy2);
            if (currentAgy2 != null)
            {
                currentAgy2.SavedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
                currentAgy2.SavedFromMachine = Environment.MachineName;
                currentAgy2.SavedAntigravityVersion = env.Version;

                bool wasUpdated;
                savedIndex = UpsertAccountInList(currentAgy2, customNoteIfNew, out wasUpdated);
                if (wasUpdated)
                {
                    Log(string.Format("Đã cập nhật token mới nhất (Antigravity 2.0) cho '{0}' (Vị trí #{1}).", currentAgy2.Email, savedIndex + 1));
                    results.Add(string.Format("• Antigravity 2.0: Đã cập nhật '{0}' (Vị trí #{1})", currentAgy2.Email, savedIndex + 1));
                }
                else
                {
                    Log(string.Format("Đã thêm mới tài khoản (Antigravity 2.0) '{0}' vào danh sách (Vị trí #{1}).", currentAgy2.Email, savedIndex + 1));
                    results.Add(string.Format("• Antigravity 2.0: Đã thêm mới '{0}' (Vị trí #{1})", currentAgy2.Email, savedIndex + 1));
                }
            }

            // 2. If Antigravity IDE is checked, also read and save/update IDE's active account
            if (Settings.ApplyToIde)
            {
                string errIde, ideRefresh, ideAccess;
                long ideExpiry;
                AccountProfile ideBasic = ReadCurrentIdeAccount(out errIde, out ideRefresh, out ideAccess, out ideExpiry);
                if (ideBasic != null && !string.IsNullOrEmpty(ideRefresh))
                {
                    bool sameAsAgy2 = (currentAgy2 != null &&
                        string.Equals(currentAgy2.Email, ideBasic.Email, StringComparison.OrdinalIgnoreCase));
                    if (!sameAsAgy2)
                    {
                        string credJson, exErr;
                        if (ExchangeRefreshTokenForCredentialJson(ideRefresh, ideAccess, ideExpiry, ideBasic.Email, ideBasic.DisplayName, ideBasic.PictureUrl, out credJson, out exErr))
                        {
                            string parseErr;
                            AccountProfile ideFull = ParseAccountFromCredentialJson(credJson, 0, out parseErr);
                            if (ideFull != null)
                            {
                                if (string.IsNullOrEmpty(ideFull.Email) || ideFull.Email.StartsWith("(")) ideFull.Email = ideBasic.Email;
                                if (string.IsNullOrEmpty(ideFull.DisplayName) || ideFull.DisplayName == ideFull.Email) ideFull.DisplayName = ideBasic.DisplayName;
                                if (string.IsNullOrEmpty(ideFull.PictureUrl)) ideFull.PictureUrl = ideBasic.PictureUrl;
                                ideFull.SavedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
                                ideFull.SavedFromMachine = Environment.MachineName;
                                ideFull.SavedAntigravityVersion = !string.IsNullOrEmpty(env.IdeVersion) && env.IdeVersion != "Chưa xác định"
                                    ? ("IDE " + env.IdeVersion)
                                    : env.Version;

                                bool wasUpdatedIde;
                                savedIndex = UpsertAccountInList(ideFull, customNoteIfNew, out wasUpdatedIde);
                                if (wasUpdatedIde)
                                {
                                    Log(string.Format("Đã cập nhật token mới nhất (Antigravity IDE) cho '{0}' (Vị trí #{1}).", ideFull.Email, savedIndex + 1));
                                    results.Add(string.Format("• Antigravity IDE: Đã cập nhật '{0}' (Vị trí #{1})", ideFull.Email, savedIndex + 1));
                                }
                                else
                                {
                                    Log(string.Format("Đã thêm mới tài khoản (Antigravity IDE) '{0}' vào danh sách (Vị trí #{1}).", ideFull.Email, savedIndex + 1));
                                    results.Add(string.Format("• Antigravity IDE: Đã thêm mới '{0}' (Vị trí #{1})", ideFull.Email, savedIndex + 1));
                                }
                            }
                        }
                    }
                    else
                    {
                        results.Add(string.Format("• Antigravity IDE: Đang dùng cùng tài khoản '{0}'", ideBasic.Email));
                    }
                }
            }

            if (results.Count == 0)
            {
                RecordError("ERR_NO_ACTIVE_ACC", errAgy2 ?? "Không tìm thấy phiên đăng nhập hiện tại.");
                message = errAgy2 ?? "Không tìm thấy phiên đăng nhập hiện tại.";
                return false;
            }

            if (results.Count == 1 && !Settings.ApplyToIde && currentAgy2 != null)
            {
                message = string.Format("Đã lưu / cập nhật phiên đăng nhập của '{0}' (Vị trí #{1})!", currentAgy2.Email, savedIndex + 1);
            }
            else
            {
                message = "Đã lưu phiên đăng nhập hiện tại vào danh sách:\n\n" + string.Join("\n", results.ToArray());
            }
            return true;
        }

        public bool ExportSelectedAccounts(List<int> selectedIndices, string filePath, out string message)
        {
            message = "";
            if (selectedIndices == null || selectedIndices.Count == 0)
            {
                message = "Vui lòng tích chọn ít nhất 1 tài khoản để xuất.";
                return false;
            }

            try
            {
                List<AccountProfile> toExport = new List<AccountProfile>();
                for (int i = 0; i < selectedIndices.Count; i++)
                {
                    int idx = selectedIndices[i];
                    if (idx >= 0 && idx < Accounts.Count)
                    {
                        toExport.Add(Accounts[idx]);
                    }
                }

                if (toExport.Count == 0)
                {
                    message = "Không tìm thấy dữ liệu tài khoản hợp lệ để xuất.";
                    return false;
                }

                for (int i = 0; i < toExport.Count; i++)
                {
                    toExport[i].SlotId = i + 1;
                }

                string json = _json.Serialize(toExport);
                File.WriteAllText(filePath, json, Utf8NoBom);
                Log(string.Format("📤 Đã xuất {0} tài khoản ra file: {1}", toExport.Count, filePath));
                message = string.Format("Đã xuất thành công {0} tài khoản ra file:\n{1}", toExport.Count, filePath);
                return true;
            }
            catch (Exception ex)
            {
                RecordError("ERR_EXPORT_SESSION", ex.Message);
                message = "Lỗi khi xuất file Session: " + ex.Message;
                return false;
            }
        }

        public bool ImportAccountsFromFile(string filePath, IWin32Window ownerWindow, out string message, out int addedCount, out int updatedCount)
        {
            message = "";
            addedCount = 0;
            updatedCount = 0;

            if (string.IsNullOrEmpty(filePath) || !File.Exists(filePath))
            {
                message = "File chọn để nhập không tồn tại.";
                return false;
            }

            try
            {
                string raw = File.ReadAllText(filePath, Encoding.UTF8).Trim();
                if (string.IsNullOrEmpty(raw))
                {
                    message = "File JSON rỗng.";
                    return false;
                }

                List<AccountProfile> importedList = null;
                if (raw.StartsWith("["))
                {
                    importedList = _json.Deserialize<List<AccountProfile>>(raw);
                }
                else if (raw.StartsWith("{"))
                {
                    AccountProfile single = _json.Deserialize<AccountProfile>(raw);
                    if (single != null)
                    {
                        importedList = new List<AccountProfile> { single };
                    }
                }

                if (importedList == null || importedList.Count == 0)
                {
                    message = "Không tìm thấy dữ liệu tài khoản nào trong file đã chọn.";
                    return false;
                }

                int skippedInvalid = 0;
                int skippedDuplicate = 0;
                foreach (AccountProfile item in importedList)
                {
                    string valErr;
                    if (!ValidateAccountProfile(item, out valErr))
                    {
                        skippedInvalid++;
                        continue;
                    }

                    int existingIdx = FindAccountIndexByEmailOrSub(item.Email, item.GoogleSub);
                    if (existingIdx >= 0)
                    {
                        AccountProfile existingAcc = Accounts[existingIdx];
                        if (ownerWindow != null)
                        {
                            DialogResult dr = MessageBox.Show(
                                ownerWindow,
                                string.Format(
                                    "Tài khoản '{0}' đã có sẵn trong danh sách.\n\n" +
                                    "• Bản hiện có trên máy (Lưu lúc: {1})\n" +
                                    "• Bản trong file đang nhập (Lưu lúc: {2})\n\n" +
                                    "Bạn có muốn GHI ĐÈ lên tài khoản này không?",
                                    item.Email,
                                    existingAcc.SavedAt ?? "N/A",
                                    item.SavedAt ?? "N/A"),
                                "Xác nhận ghi đè tài khoản trùng",
                                MessageBoxButtons.YesNo,
                                MessageBoxIcon.Question);

                            if (dr != DialogResult.Yes)
                            {
                                skippedDuplicate++;
                                Log(string.Format("Đã bỏ qua không ghi đè tài khoản trùng '{0}'.", item.Email));
                                continue;
                            }
                        }

                        if (string.IsNullOrEmpty(item.Note))
                        {
                            item.Note = existingAcc.Note;
                        }
                        Accounts[existingIdx] = item;
                        updatedCount++;
                    }
                    else
                    {
                        Accounts.Add(item);
                        addedCount++;
                    }
                }

                if (addedCount == 0 && updatedCount == 0 && skippedDuplicate == 0)
                {
                    message = "Không nhập được tài khoản nào (dữ liệu trong file không đúng chuẩn Session của Antigravity).";
                    return false;
                }

                if (addedCount > 0 || updatedCount > 0)
                {
                    SaveAccounts();
                }

                Log(string.Format("📥 Đã nhập Session từ '{0}': Thêm mới {1}, Ghi đè {2}, Bỏ qua trùng {3} (Lỗi: {4}).",
                    Path.GetFileName(filePath), addedCount, updatedCount, skippedDuplicate, skippedInvalid));
                message = string.Format(
                    "Hoàn tất nhập dữ liệu Session!\n\n" +
                    "• Thêm mới: {0} tài khoản\n" +
                    "• Đã ghi đè: {1} tài khoản\n" +
                    "• Đã bỏ qua (giữ bản cũ): {2} tài khoản" +
                    (skippedInvalid > 0 ? string.Format("\n• Mục không hợp lệ: {0}", skippedInvalid) : ""),
                    addedCount, updatedCount, skippedDuplicate);
                return true;
            }
            catch (Exception ex)
            {
                RecordError("ERR_IMPORT_SESSION", ex.Message);
                message = "Lỗi khi đọc file nhập Session: " + ex.Message;
                return false;
            }
        }

        public bool ValidateAccountProfile(AccountProfile profile, out string validationError)
        {
            validationError = null;
            if (profile == null)
            {
                validationError = "Tài khoản được chọn không tồn tại trong danh sách.";
                return false;
            }
            if (string.IsNullOrEmpty(profile.CredentialJson))
            {
                validationError = "Dữ liệu Credential của tài khoản bị trống.";
                return false;
            }
            try
            {
                Dictionary<string, object> root = _json.Deserialize<Dictionary<string, object>>(profile.CredentialJson);
                if (root == null || !root.ContainsKey("token"))
                {
                    validationError = "Dữ liệu JSON của tài khoản bị hỏng (thiếu trường 'token').";
                    return false;
                }
                Dictionary<string, object> tok = root["token"] as Dictionary<string, object>;
                if (tok == null || !tok.ContainsKey("refresh_token") || string.IsNullOrEmpty(Convert.ToString(tok["refresh_token"])))
                {
                    validationError = "Phiên đã lưu không có 'refresh_token', không thể duy trì đăng nhập.";
                    return false;
                }
            }
            catch (Exception ex)
            {
                validationError = "Lỗi đọc JSON của tài khoản: " + ex.Message;
                return false;
            }
            return true;
        }

        public bool CloseAntigravityProcesses(out string closeError)
        {
            closeError = null;
            try
            {
                Process[] agyProcs = Process.GetProcessesByName("Antigravity");
                if (agyProcs.Length > 0)
                {
                    Log(string.Format("Đang gửi tín hiệu đóng {0} tiến trình Antigravity.exe...", agyProcs.Length));
                    foreach (Process p in agyProcs)
                    {
                        try
                        {
                            if (p.MainWindowHandle != IntPtr.Zero)
                            {
                                p.CloseMainWindow();
                            }
                        }
                        catch { }
                    }

                    DateTime deadline = DateTime.Now.AddMilliseconds(3500);
                    while (DateTime.Now < deadline)
                    {
                        if (Process.GetProcessesByName("Antigravity").Length == 0 &&
                            Process.GetProcessesByName("language_server").Length == 0)
                        {
                            break;
                        }
                        Thread.Sleep(250);
                    }
                }

                foreach (string procName in new string[] { "Antigravity", "language_server" })
                {
                    Process[] remaining = Process.GetProcessesByName(procName);
                    foreach (Process p in remaining)
                    {
                        try
                        {
                            Log(string.Format("Đang buộc dừng tiến trình {0} (PID: {1})...", procName, p.Id));
                            p.Kill();
                            p.WaitForExit(3000);
                        }
                        catch (Exception kex)
                        {
                            if (!p.HasExited)
                            {
                                closeError = string.Format("Không thể đóng tiến trình {0} (PID {1}): {2}", procName, p.Id, kex.Message);
                                return false;
                            }
                        }
                    }
                }

                DateTime verifyDeadline = DateTime.Now.AddMilliseconds(2000);
                while (DateTime.Now < verifyDeadline)
                {
                    if (Process.GetProcessesByName("Antigravity").Length == 0 &&
                        Process.GetProcessesByName("language_server").Length == 0)
                    {
                        Thread.Sleep(300);
                        Log("Đã xác nhận mọi tiến trình Antigravity và language_server đã đóng hoàn toàn.");
                        return true;
                    }
                    Thread.Sleep(200);
                }

                int remAgy = Process.GetProcessesByName("Antigravity").Length;
                int remLs = Process.GetProcessesByName("language_server").Length;
                if (remAgy > 0 || remLs > 0)
                {
                    closeError = string.Format("Tiến trình vẫn còn chạy sau khi đóng (Antigravity: {0}, language_server: {1}).", remAgy, remLs);
                    return false;
                }

                return true;
            }
            catch (Exception ex)
            {
                closeError = "Lỗi khi đóng tiến trình Antigravity: " + ex.Message;
                return false;
            }
        }

        public bool CloseAntigravityIdeProcesses(out string closeError)
        {
            closeError = null;
            try
            {
                Process[] ideProcs = Process.GetProcessesByName("Antigravity IDE");
                if (ideProcs.Length > 0)
                {
                    Log(string.Format("Đang gửi tín hiệu đóng {0} tiến trình Antigravity IDE.exe...", ideProcs.Length));
                    foreach (Process p in ideProcs)
                    {
                        try
                        {
                            if (p.MainWindowHandle != IntPtr.Zero)
                            {
                                p.CloseMainWindow();
                            }
                        }
                        catch { }
                    }

                    DateTime deadline = DateTime.Now.AddMilliseconds(3500);
                    while (DateTime.Now < deadline)
                    {
                        if (Process.GetProcessesByName("Antigravity IDE").Length == 0 &&
                            Process.GetProcessesByName("language_server_windows_x64").Length == 0)
                        {
                            break;
                        }
                        Thread.Sleep(250);
                    }
                }

                foreach (string procName in new string[] { "Antigravity IDE", "language_server_windows_x64" })
                {
                    Process[] remaining = Process.GetProcessesByName(procName);
                    foreach (Process p in remaining)
                    {
                        try
                        {
                            Log(string.Format("Đang buộc dừng tiến trình {0} (PID: {1})...", procName, p.Id));
                            p.Kill();
                            p.WaitForExit(3000);
                        }
                        catch (Exception kex)
                        {
                            if (!p.HasExited)
                            {
                                closeError = string.Format("Không thể đóng tiến trình {0} (PID {1}): {2}", procName, p.Id, kex.Message);
                                return false;
                            }
                        }
                    }
                }

                DateTime verifyDeadline = DateTime.Now.AddMilliseconds(2000);
                while (DateTime.Now < verifyDeadline)
                {
                    if (Process.GetProcessesByName("Antigravity IDE").Length == 0 &&
                        Process.GetProcessesByName("language_server_windows_x64").Length == 0)
                    {
                        Thread.Sleep(300);
                        Log("Đã xác nhận mọi tiến trình Antigravity IDE đã đóng hoàn toàn.");
                        return true;
                    }
                    Thread.Sleep(200);
                }

                int remIde = Process.GetProcessesByName("Antigravity IDE").Length;
                int remLs = Process.GetProcessesByName("language_server_windows_x64").Length;
                if (remIde > 0 || remLs > 0)
                {
                    closeError = string.Format("Tiến trình Antigravity IDE vẫn còn chạy sau khi đóng (IDE: {0}, language_server_x64: {1}).", remIde, remLs);
                    return false;
                }

                return true;
            }
            catch (Exception ex)
            {
                closeError = "Lỗi khi đóng tiến trình Antigravity IDE: " + ex.Message;
                return false;
            }
        }

        public bool SwitchToAccountIndex(int targetIndex, IWin32Window ownerWindow, out string resultMessage)
        {
            resultMessage = "";
            if (targetIndex < 0 || targetIndex >= Accounts.Count)
            {
                resultMessage = "Vị trí tài khoản trong danh sách không hợp lệ.";
                return false;
            }

            bool applyIde = Settings.ApplyToIde;
            AccountProfile targetProfile = Accounts[targetIndex];
            Log(string.Format("=== Bắt đầu quy trình chuyển sang tài khoản #{0}: {1} (Áp dụng IDE: {2}) ===",
                targetIndex + 1, targetProfile.Email, applyIde ? "Có" : "Không"));

            // 1. Pre-flight check: Validate target account
            string valErr;
            if (!ValidateAccountProfile(targetProfile, out valErr))
            {
                RecordError("ERR_PREFLIGHT_INVALID_ACC", valErr);
                resultMessage = valErr;
                return false;
            }

            // 2. Pre-flight check: Inspect environment
            EnvironmentStatus env = InspectEnvironment();
            if (!env.IsInstalled)
            {
                Log("Cảnh báo: Chưa tìm thấy file Antigravity.exe trên máy này, nhưng vẫn có thể nạp trước phiên đăng nhập.");
            }

            // 3. Auto-backup current active account before touching anything
            string activeErr;
            AccountProfile currentActive = ReadCurrentActiveAccount(out activeErr);
            if (currentActive != null)
            {
                try
                {
                    currentActive.SavedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
                    currentActive.SavedFromMachine = Environment.MachineName;
                    currentActive.SavedAntigravityVersion = env.Version;
                    File.WriteAllText(RollbackBackupPath, _json.Serialize(currentActive), Utf8NoBom);
                    Log("Đã tạo điểm khôi phục (Rollback Backup) cho tài khoản hiện tại: " + currentActive.Email);

                    int activeIdx = FindAccountIndexByEmailOrSub(currentActive.Email, currentActive.GoogleSub);
                    if (activeIdx >= 0)
                    {
                        currentActive.Note = Accounts[activeIdx].Note;
                        Accounts[activeIdx] = currentActive;
                        SaveAccounts();
                        Log(string.Format("Đã tự động cập nhật token mới nhất của '{0}' vào danh sách (#{1}).", currentActive.Email, activeIdx + 1));
                    }
                }
                catch (Exception bex)
                {
                    Log("Cảnh báo khi lưu bản backup tạm: " + bex.Message);
                }
            }

            // 4. Pre-flight check: Check if target app(s) are running & confirm / close
            env = InspectEnvironment();
            bool needCloseAgy2 = env.IsRunning;
            bool needCloseIde = applyIde && env.IdeIsRunning;

            if (needCloseAgy2 || needCloseIde)
            {
                if (Settings.ConfirmBeforeClosingApp && ownerWindow != null)
                {
                    List<string> runningApps = new List<string>();
                    if (needCloseAgy2) runningApps.Add(string.Format("Antigravity 2.0 (PID: [{0}])", string.Join(", ", env.AntigravityPids)));
                    if (needCloseIde) runningApps.Add(string.Format("Antigravity IDE (PID: [{0}])", string.Join(", ", env.IdePids)));
                    string appNames = needCloseAgy2 && needCloseIde ? "Antigravity 2.0 và Antigravity IDE" : (needCloseIde ? "Antigravity IDE" : "Antigravity 2.0");

                    DialogResult dr = MessageBox.Show(
                        ownerWindow,
                        string.Format(
                            "Ứng dụng đang chạy:\n• {0}\n\n" +
                            "⚠️ Lưu ý: Để đổi tài khoản an toàn, chương trình sẽ đóng {1}, nạp phiên của '{2}' ({3}), rồi mở lại ứng dụng.\n" +
                            "Hãy đảm bảo không có Agent nào đang thực hiện dở tác vụ quan trọng.\n\n" +
                            "Bạn có muốn tiếp tục chuyển đổi ngay không?",
                            string.Join("\n• ", runningApps.ToArray()), appNames, targetProfile.Email, targetProfile.Note ?? ""),
                        "Xác nhận đóng ứng dụng để đổi tài khoản",
                        MessageBoxButtons.YesNo,
                        MessageBoxIcon.Question);

                    if (dr != DialogResult.Yes)
                    {
                        Log("Người dùng đã hủy thao tác chuyển tài khoản ở bước xác nhận đóng app.");
                        resultMessage = "Đã hủy thao tác chuyển đổi theo yêu cầu của bạn.";
                        return false;
                    }
                }

                if (needCloseAgy2)
                {
                    string closeErr;
                    if (!CloseAntigravityProcesses(out closeErr))
                    {
                        RecordError("ERR_PREFLIGHT_APP_NOT_CLOSED", closeErr);
                        resultMessage = "Không thể chuyển tài khoản vì ứng dụng Antigravity 2.0 chưa đóng hoàn toàn:\n" + closeErr;
                        return false;
                    }
                }

                if (needCloseIde)
                {
                    string closeIdeErr;
                    if (!CloseAntigravityIdeProcesses(out closeIdeErr))
                    {
                        RecordError("ERR_PREFLIGHT_IDE_NOT_CLOSED", closeIdeErr);
                        resultMessage = "Không thể chuyển tài khoản vì ứng dụng Antigravity IDE chưa đóng hoàn toàn:\n" + closeIdeErr;
                        return false;
                    }
                }
            }

            // 5. Apply Credential to Windows Credential Manager (Antigravity 2.0) & Verify
            try
            {
                int win32Err;
                if (!NativeCred.WriteAntigravityCredential(targetProfile.CredentialJson, out win32Err))
                {
                    string errText = string.Format("Ghi Credential gemini:antigravity thất bại (Win32 Error: {0}).", win32Err);
                    RecordError("ERR_CRED_WRITE", errText);
                    TryRollback();
                    resultMessage = errText;
                    return false;
                }

                string verifyErr;
                AccountProfile verified = ReadCurrentActiveAccount(out verifyErr);
                if (verified == null)
                {
                    string errText = "Xác minh sau khi ghi Credential thất bại: " + (verifyErr ?? "Unknown");
                    RecordError("ERR_CRED_VERIFY", errText);
                    TryRollback();
                    resultMessage = errText;
                    return false;
                }

                UpdateAppStorageJson(env, targetProfile);
                EnsureOnboardingStatePbtxt(env);

                Log(string.Format("✅ Đã chuyển đổi Antigravity 2.0 sang tài khoản #{0}: {1}.", targetIndex + 1, targetProfile.Email));
            }
            catch (Exception ex)
            {
                RecordError("ERR_SWITCH_EXCEPTION", ex.ToString());
                TryRollback();
                resultMessage = "Gặp lỗi trong quá trình nạp tài khoản (đã khôi phục lại trạng thái cũ): " + ex.Message;
                return false;
            }

            // 5b. If Antigravity IDE checkbox is checked, also write to Antigravity IDE (state.vscdb)
            string ideWarning = null;
            if (applyIde)
            {
                string ideWriteErr;
                if (WriteAccountToIde(env, targetProfile, out ideWriteErr))
                {
                    Log(string.Format("✅ Đã chuyển đổi Antigravity IDE sang tài khoản #{0}: {1}.", targetIndex + 1, targetProfile.Email));
                }
                else
                {
                    RecordError("ERR_IDE_WRITE", ideWriteErr ?? "Unknown");
                    ideWarning = ideWriteErr;
                }
            }

            // 6. Relaunch target app(s) if enabled and installed
            if (Settings.AutoRelaunchAfterSwitch)
            {
                env = InspectEnvironment();
                List<string> launched = new List<string>();
                if (env.IsInstalled)
                {
                    try
                    {
                        Log("Đang khởi động lại Antigravity 2.0: " + env.ExePath);
                        ProcessStartInfo psi = new ProcessStartInfo();
                        psi.FileName = env.ExePath;
                        psi.WorkingDirectory = Path.GetDirectoryName(env.ExePath);
                        psi.UseShellExecute = true;
                        Process.Start(psi);
                        launched.Add("Antigravity 2.0");
                    }
                    catch (Exception lex)
                    {
                        RecordError("ERR_RELAUNCH_FAILED", lex.Message);
                    }
                }

                if (applyIde && env.IdeIsInstalled && string.IsNullOrEmpty(ideWarning))
                {
                    try
                    {
                        Log("Đang khởi động lại Antigravity IDE: " + env.IdeExePath);
                        ProcessStartInfo psiIde = new ProcessStartInfo();
                        psiIde.FileName = env.IdeExePath;
                        psiIde.WorkingDirectory = Path.GetDirectoryName(env.IdeExePath);
                        psiIde.UseShellExecute = true;
                        Process.Start(psiIde);
                        launched.Add("Antigravity IDE");
                    }
                    catch (Exception lex)
                    {
                        RecordError("ERR_RELAUNCH_IDE_FAILED", lex.Message);
                    }
                }

                string targetDesc = applyIde ? "Antigravity 2.0 + IDE" : "Antigravity 2.0";
                if (!string.IsNullOrEmpty(ideWarning))
                {
                    resultMessage = string.Format("Đã chuyển Antigravity 2.0 sang {0} (#{1}), nhưng Antigravity IDE gặp cảnh báo: {2}",
                        targetProfile.Email, targetIndex + 1, ideWarning);
                    return true;
                }
                if (launched.Count > 0)
                {
                    resultMessage = string.Format("Đã chuyển sang tài khoản {0} (#{1}) và mở lại {2}!",
                        targetProfile.Email, targetIndex + 1, string.Join(" + ", launched.ToArray()));
                    return true;
                }
                resultMessage = string.Format("Đã nạp sẵn phiên của {0} ({1}) vào máy!", targetProfile.Email, targetDesc);
                return true;
            }

            if (!string.IsNullOrEmpty(ideWarning))
            {
                resultMessage = string.Format("Đã chuyển Antigravity 2.0 sang {0} (#{1}), nhưng Antigravity IDE gặp cảnh báo: {2}",
                    targetProfile.Email, targetIndex + 1, ideWarning);
                return true;
            }

            resultMessage = string.Format("Đã chuyển {0} sang tài khoản {1} (#{2}) thành công!",
                applyIde ? "Antigravity 2.0 + Antigravity IDE" : "Antigravity 2.0",
                targetProfile.Email, targetIndex + 1);
            return true;
        }

        private void UpdateAppStorageJson(EnvironmentStatus env, AccountProfile targetProfile)
        {
            try
            {
                if (!Directory.Exists(env.AppDataRoamingDir))
                {
                    Directory.CreateDirectory(env.AppDataRoamingDir);
                }

                Dictionary<string, object> storage = null;
                if (File.Exists(env.AppStorageJsonPath))
                {
                    // If the existing file can't be read, leave it alone: rewriting it would wipe Antigravity's other settings
                    string existing = File.ReadAllText(env.AppStorageJsonPath, Encoding.UTF8);
                    if (!string.IsNullOrEmpty(existing.Trim()))
                    {
                        storage = _json.Deserialize<Dictionary<string, object>>(existing);
                    }
                }

                if (storage == null)
                {
                    storage = new Dictionary<string, object>();
                }

                if (!string.IsNullOrEmpty(targetProfile.Email) && !targetProfile.Email.StartsWith("("))
                {
                    storage["jetski.onboarding.lastLoginUsername"] = targetProfile.Email;
                }
                storage["jetski.onboarding.lastLoginIsGcpTos"] = string.IsNullOrEmpty(targetProfile.LastLoginIsGcpTos) ? "false" : targetProfile.LastLoginIsGcpTos;

                string output = _json.Serialize(storage);
                File.WriteAllText(env.AppStorageJsonPath, output, Utf8NoBom);
                Log("Đã đồng bộ jetski.onboarding.lastLoginUsername trong app_storage.json.");
            }
            catch (Exception ex)
            {
                Log("Cảnh báo khi cập nhật app_storage.json: " + ex.Message);
            }
        }

        private void EnsureOnboardingStatePbtxt(EnvironmentStatus env)
        {
            try
            {
                if (!Directory.Exists(env.GeminiAntigravityDir))
                {
                    Directory.CreateDirectory(env.GeminiAntigravityDir);
                }

                if (!File.Exists(env.StatePbtxtPath))
                {
                    string defaultPbtxt =
                        "post_onboarding: {\n" +
                        "  completed_steps: POST_ONBOARDING_STEP_TYPE_MANAGER_WELCOME\n" +
                        "  completed_steps: POST_ONBOARDING_STEP_TYPE_USAGE_MODE\n" +
                        "  completed_steps: POST_ONBOARDING_STEP_TYPE_AGENT_CONFIGURATION\n" +
                        "  completed_steps: POST_ONBOARDING_STEP_TYPE_ADD_WORKSPACE\n" +
                        "}\n" +
                        "agent_onboarding_completed: AGENT_ONBOARDING_STATE_COMPLETED\n";
                    File.WriteAllText(env.StatePbtxtPath, defaultPbtxt, Utf8NoBom);
                    Log("Đã khởi tạo antigravity_state.pbtxt (bỏ qua màn hình Onboarding trên máy mới).");
                }
            }
            catch (Exception ex)
            {
                Log("Cảnh báo khi kiểm tra antigravity_state.pbtxt: " + ex.Message);
            }
        }

        private void TryRollback()
        {
            try
            {
                if (File.Exists(RollbackBackupPath))
                {
                    string raw = File.ReadAllText(RollbackBackupPath, Encoding.UTF8);
                    AccountProfile backup = _json.Deserialize<AccountProfile>(raw);
                    if (backup != null && !string.IsNullOrEmpty(backup.CredentialJson))
                    {
                        int wErr;
                        NativeCred.WriteAntigravityCredential(backup.CredentialJson, out wErr);
                        Log("🔄 Đã tự động khôi phục (Rollback) lại phiên đăng nhập cũ: " + backup.Email);
                    }
                }
            }
            catch (Exception ex)
            {
                Log("Lỗi khi thực hiện Rollback: " + ex.Message);
            }
        }

        public string BuildDiagnosticsReport()
        {
            EnvironmentStatus env = InspectEnvironment();
            string activeErr;
            AccountProfile active = ReadCurrentActiveAccount(out activeErr);
            string ideErr, ideRef, ideAcc;
            long ideExp;
            AccountProfile ideActive = ReadCurrentIdeAccount(out ideErr, out ideRef, out ideAcc, out ideExp);

            StringBuilder sb = new StringBuilder();
            sb.AppendLine("============================================================");
            sb.AppendLine("   BÁO CÁO CHẨN ĐOÁN & THÔNG TIN HỆ THỐNG ANTIGRAVITY");
            sb.AppendLine("============================================================");
            sb.AppendFormat("Thời gian tạo báo cáo : {0:yyyy-MM-dd HH:mm:ss}\r\n", DateTime.Now);
            sb.AppendFormat("Tên máy (MachineName) : {0}\r\n", Environment.MachineName);
            sb.AppendFormat("Hệ điều hành (OS)     : {0} (64-bit: {1})\r\n", Environment.OSVersion, Environment.Is64BitOperatingSystem);
            sb.AppendFormat(".NET Runtime          : {0}\r\n", Environment.Version);
            sb.AppendFormat("Thư mục chạy Tool     : {0}\r\n", BaseDir);
            sb.AppendLine("------------------------------------------------------------");
            sb.AppendLine("[1] TRẠNG THÁI CÀI ĐẶT & PHIÊN BẢN ANTIGRAVITY 2.0");
            sb.AppendFormat("- Đã cài đặt          : {0}\r\n", env.IsInstalled ? "CÓ (Installed)" : "KHÔNG TÌM THẤY (Not Found)");
            sb.AppendFormat("- Phiên bản hiện tại  : {0}\r\n", env.Version);
            sb.AppendFormat("- Đường dẫn Exe       : {0}\r\n", env.ExePath ?? "(Không tìm thấy)");
            sb.AppendFormat("- Nguồn phát hiện     : {0}\r\n", env.DetectionSource ?? "N/A");
            sb.AppendFormat("- Đang chạy           : {0}\r\n", env.IsRunning ? "ĐANG CHẠY" : "ĐÃ ĐÓNG");
            sb.AppendFormat("- PID Antigravity.exe : [{0}]\r\n", string.Join(", ", env.AntigravityPids));
            sb.AppendFormat("- PID language_server : [{0}]\r\n", string.Join(", ", env.LanguageServerPids));
            sb.AppendLine("------------------------------------------------------------");
            sb.AppendLine("[1B] TRẠNG THÁI ANTIGRAVITY IDE");
            sb.AppendFormat("- Áp dụng cho IDE     : {0}\r\n", Settings.ApplyToIde ? "BẬT" : "TẮT");
            sb.AppendFormat("- Đã cài đặt IDE      : {0}\r\n", env.IdeIsInstalled ? "CÓ (Installed)" : "KHÔNG TÌM THẤY (Not Found)");
            sb.AppendFormat("- Phiên bản IDE       : {0}\r\n", env.IdeVersion);
            sb.AppendFormat("- Đường dẫn IDE Exe   : {0}\r\n", env.IdeExePath ?? "(Không tìm thấy)");
            sb.AppendFormat("- Đang chạy IDE       : {0}\r\n", env.IdeIsRunning ? "ĐANG CHẠY" : "ĐÃ ĐÓNG");
            sb.AppendFormat("- PID Antigravity IDE : [{0}]\r\n", string.Join(", ", env.IdePids));
            sb.AppendFormat("- state.vscdb         : {0} (Tồn tại: {1})\r\n", env.IdeStateVscdbPath, env.IdeStateVscdbExists);
            sb.AppendFormat("- Tài khoản Active IDE: {0}\r\n", ideActive != null ? (ideActive.Email + " (" + ideActive.DisplayName + ")") : ("Chưa đăng nhập (" + (ideErr ?? "N/A") + ")"));
            sb.AppendLine("------------------------------------------------------------");
            sb.AppendLine("[2] ĐƯỜNG DẪN DỮ LIỆU ĐỘNG (KHÔNG HARDCODE)");
            sb.AppendFormat("- app_storage.json    : {0} (Tồn tại: {1})\r\n", env.AppStorageJsonPath, env.AppStorageJsonExists);
            sb.AppendFormat("- state.pbtxt         : {0} (Tồn tại: {1})\r\n", env.StatePbtxtPath, env.StatePbtxtExists);
            sb.AppendFormat("- lockfile            : {0} (Tồn tại: {1})\r\n", env.LockfilePath, env.LockfileExists);
            sb.AppendLine("------------------------------------------------------------");
            sb.AppendFormat("[3] DANH SÁCH TÀI KHOẢN ĐÃ LƯU ({0} TÀI KHOẢN)\r\n", Accounts.Count);
            if (active != null)
            {
                sb.AppendFormat("- Tài khoản Active    : {0} ({1}) | Expiry: {2}\r\n", active.Email, active.DisplayName, active.TokenExpiry);
            }
            else
            {
                sb.AppendFormat("- Tài khoản Active    : Chưa đăng nhập ({0})\r\n", activeErr);
            }
            for (int i = 0; i < Accounts.Count; i++)
            {
                AccountProfile a = Accounts[i];
                sb.AppendFormat("  #{0}: {1} | Ghi chú: {2} | Lưu lúc: {3} | Máy: {4} | AGY Ver: {5}\r\n",
                    i + 1, a.Email, a.Note ?? "", a.SavedAt, a.SavedFromMachine, a.SavedAntigravityVersion);
            }
            sb.AppendLine("------------------------------------------------------------");
            sb.AppendLine("[4] MÃ LỖI GẦN NHẤT (LAST ERROR)");
            sb.AppendFormat("- Mã lỗi (Error Code) : {0}\r\n", LastErrorCode);
            sb.AppendFormat("- Chi tiết lỗi        : {0}\r\n", LastErrorDetail);
            sb.AppendLine("------------------------------------------------------------");
            sb.AppendLine("[5] NHẬT KÝ HOẠT ĐỘNG GẦN ĐÂY (RECENT LOGS)");
            string[] logs = GetActivityLogSnapshot();
            for (int i = Math.Max(0, logs.Length - 25); i < logs.Length; i++)
            {
                sb.AppendLine("  " + logs[i]);
            }
            sb.AppendLine("============================================================");
            return sb.ToString();
        }
    }

    // =========================================================================
    // UI Theme: Light / Dark palettes, fonts, icon glyphs & drawing helpers
    // =========================================================================
    internal sealed class UiPalette
    {
        public bool IsDark;
        public Color WindowBg, CardBg, CardBorder, Text, TextSecondary, TextMuted;
        public Color Accent, AccentHover, AccentPressed, AccentText, AccentSubText;
        public Color ControlBg, ControlBorder, ControlBorderHover, InputBg;
        public Color Success, SuccessBg, Warning, WarningBg, Danger;
        public Color ChipBg, ChipText, ChipDot, AccentChipBg, AccentChipText;
        public Color ActiveRowBg, SelectedRowBg, HeaderRowBg, RowDivider;
        public Color LogBg, LogText, LinkHover;
        public Color HoverOverlay, PressedOverlay, DangerOverlay;

        private static Color Hex(string hex)
        {
            return ColorTranslator.FromHtml(hex);
        }

        public static UiPalette CreateLight()
        {
            UiPalette p = new UiPalette();
            p.IsDark = false;
            p.WindowBg = Hex("#F3F3F3");
            p.CardBg = Hex("#FFFFFF");
            p.CardBorder = Hex("#E5E5E5");
            p.Text = Hex("#1B1B1B");
            p.TextSecondary = Hex("#5F5F5F");
            p.TextMuted = Hex("#8A8A8A");
            p.Accent = Hex("#0067C0");
            p.AccentHover = Hex("#1975C5");
            p.AccentPressed = Hex("#3183CA");
            p.AccentText = Hex("#FFFFFF");
            p.AccentSubText = Hex("#DCEBFA");
            p.ControlBg = Hex("#FFFFFF");
            p.ControlBorder = Hex("#D1D1D1");
            p.ControlBorderHover = Hex("#B5B5B5");
            p.InputBg = Hex("#FFFFFF");
            p.Success = Hex("#0E700E");
            p.SuccessBg = Hex("#DFF6DD");
            p.Warning = Hex("#8A5300");
            p.WarningBg = Hex("#FFF4CE");
            p.Danger = Hex("#C42B1C");
            p.ChipBg = Hex("#F0F0F0");
            p.ChipText = Hex("#424242");
            p.ChipDot = Hex("#8A8A8A");
            p.AccentChipBg = Hex("#E5F0FB");
            p.AccentChipText = Hex("#005A9E");
            p.ActiveRowBg = Hex("#F1FAF1");
            p.SelectedRowBg = Hex("#E5F0FB");
            p.HeaderRowBg = Hex("#FAFAFA");
            p.RowDivider = Hex("#EFEFEF");
            p.LogBg = Hex("#F7F7F7");
            p.LogText = Hex("#2B2B2B");
            p.LinkHover = Hex("#003E92");
            p.HoverOverlay = Color.FromArgb(14, 0, 0, 0);
            p.PressedOverlay = Color.FromArgb(26, 0, 0, 0);
            p.DangerOverlay = Color.FromArgb(24, 196, 43, 28);
            return p;
        }

        public static UiPalette CreateDark()
        {
            UiPalette p = new UiPalette();
            p.IsDark = true;
            p.WindowBg = Hex("#202020");
            p.CardBg = Hex("#2B2B2B");
            p.CardBorder = Hex("#383838");
            p.Text = Hex("#FFFFFF");
            p.TextSecondary = Hex("#C8C8C8");
            p.TextMuted = Hex("#9E9E9E");
            p.Accent = Hex("#4CC2FF");
            p.AccentHover = Hex("#48B1E8");
            p.AccentPressed = Hex("#42A1D2");
            p.AccentText = Hex("#000000");
            p.AccentSubText = Hex("#0B2A3B");
            p.ControlBg = Hex("#2D2D2D");
            p.ControlBorder = Hex("#4A4A4A");
            p.ControlBorderHover = Hex("#5E5E5E");
            p.InputBg = Hex("#1F1F1F");
            p.Success = Hex("#8FE38A");
            p.SuccessBg = Hex("#1E3A1F");
            p.Warning = Hex("#FCE100");
            p.WarningBg = Hex("#433519");
            p.Danger = Hex("#FF99A4");
            p.ChipBg = Hex("#383838");
            p.ChipText = Hex("#E0E0E0");
            p.ChipDot = Hex("#9E9E9E");
            p.AccentChipBg = Hex("#1C3245");
            p.AccentChipText = Hex("#99DCFF");
            p.ActiveRowBg = Hex("#22301F");
            p.SelectedRowBg = Hex("#1C3245");
            p.HeaderRowBg = Hex("#262626");
            p.RowDivider = Hex("#333333");
            p.LogBg = Hex("#1C1C1C");
            p.LogText = Hex("#D6D6D6");
            p.LinkHover = Hex("#99DCFF");
            p.HoverOverlay = Color.FromArgb(18, 255, 255, 255);
            p.PressedOverlay = Color.FromArgb(10, 255, 255, 255);
            p.DangerOverlay = Color.FromArgb(30, 255, 153, 164);
            return p;
        }
    }

    // Segoe Fluent Icons (Win 11) / Segoe MDL2 Assets (Win 10) code points
    internal static class Glyph
    {
        public const string Switch = "";
        public const string Add = "";
        public const string Upload = "";
        public const string Download = "";
        public const string Edit = "";
        public const string Up = "";
        public const string Down = "";
        public const string Delete = "";
        public const string Refresh = "";
        public const string Copy = "";
        public const string Accept = "";
        public const string ChevronRight = "";
        public const string ChevronDown = "";
        public const string Error = "";
    }

    internal enum ChipKind { Neutral, Success, Warning, Accent }
    internal enum ButtonKind { Primary, AccentOutline, Outline, Ghost, Danger }
    internal enum LabelRole { Primary, Secondary, Muted }

    internal interface IThemed
    {
        void ApplyTheme();
    }

    internal static class UiTheme
    {
        public static readonly UiPalette Light = UiPalette.CreateLight();
        public static readonly UiPalette Dark = UiPalette.CreateDark();
        public static UiPalette Current = Light;
        public static string Mode = "System";

        private static float _scale;
        private static string _iconFontName;
        private static bool _iconFontChecked;
        private static ThemedMenuRenderer _menuRenderer;
        private static readonly Dictionary<string, Font> _fontCache = new Dictionary<string, Font>();

        private const uint SWP_NOSIZE = 0x0001;
        private const uint SWP_NOMOVE = 0x0002;
        private const uint SWP_NOZORDER = 0x0004;
        private const uint SWP_NOACTIVATE = 0x0010;
        private const uint SWP_FRAMECHANGED = 0x0020;

        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

        [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)]
        private static extern int SetWindowTheme(IntPtr hWnd, string pszSubAppName, string pszSubIdList);

        [DllImport("user32.dll")]
        private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint uFlags);

        public static float Scale
        {
            get
            {
                if (_scale <= 0f)
                {
                    try
                    {
                        using (Graphics g = Graphics.FromHwnd(IntPtr.Zero))
                        {
                            _scale = g.DpiX / 96f;
                        }
                    }
                    catch
                    {
                        _scale = 1f;
                    }
                    if (_scale <= 0f) _scale = 1f;
                }
                return _scale;
            }
        }

        // Scale a 96-DPI pixel value to the current screen DPI
        public static int S(int px)
        {
            return (int)Math.Round(px * Scale);
        }

        public static string NormalizeMode(string mode)
        {
            if (string.Equals(mode, "Light", StringComparison.OrdinalIgnoreCase)) return "Light";
            if (string.Equals(mode, "Dark", StringComparison.OrdinalIgnoreCase)) return "Dark";
            return "System";
        }

        public static bool IsWindowsDarkMode()
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"))
                {
                    if (key != null)
                    {
                        object v = key.GetValue("AppsUseLightTheme");
                        if (v is int) return (int)v == 0;
                    }
                }
            }
            catch { }
            return false;
        }

        // Returns true when the active palette actually changed
        public static bool ApplyMode(string mode)
        {
            Mode = NormalizeMode(mode);
            bool dark = Mode == "Dark" || (Mode == "System" && IsWindowsDarkMode());
            UiPalette next = dark ? Dark : Light;
            bool changed = !object.ReferenceEquals(next, Current);
            Current = next;
            return changed;
        }

        public static Font GetFont(float size, bool semibold)
        {
            return GetCachedFont(semibold ? "Segoe UI Semibold" : "Segoe UI", size);
        }

        public static Font GetMonoFont(float size)
        {
            return GetCachedFont("Consolas", size);
        }

        public static Font GetIconFont(float size)
        {
            string name = IconFontName;
            return name == null ? null : GetCachedFont(name, size);
        }

        private static Font GetCachedFont(string family, float size)
        {
            string key = family + "|" + size.ToString(System.Globalization.CultureInfo.InvariantCulture);
            Font f;
            if (!_fontCache.TryGetValue(key, out f))
            {
                f = new Font(family, size, FontStyle.Regular);
                _fontCache[key] = f;
            }
            return f;
        }

        public static string IconFontName
        {
            get
            {
                if (!_iconFontChecked)
                {
                    _iconFontChecked = true;
                    try
                    {
                        using (InstalledFontCollection fonts = new InstalledFontCollection())
                        {
                            foreach (string candidate in new string[] { "Segoe Fluent Icons", "Segoe MDL2 Assets" })
                            {
                                foreach (FontFamily ff in fonts.Families)
                                {
                                    if (string.Equals(ff.Name, candidate, StringComparison.OrdinalIgnoreCase))
                                    {
                                        _iconFontName = candidate;
                                        break;
                                    }
                                }
                                if (_iconFontName != null) break;
                            }
                        }
                    }
                    catch { }
                }
                return _iconFontName;
            }
        }

        public static GraphicsPath RoundRect(Rectangle r, int radius)
        {
            GraphicsPath path = new GraphicsPath();
            int d = Math.Min(radius * 2, Math.Min(r.Width, r.Height));
            if (d <= 1)
            {
                path.AddRectangle(r);
                return path;
            }
            path.AddArc(r.X, r.Y, d, d, 180, 90);
            path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }

        // Pass Color.Empty to skip the fill or the border
        public static void FillRound(Graphics g, Rectangle r, int radius, Color fill, Color border)
        {
            SmoothingMode old = g.SmoothingMode;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (GraphicsPath path = RoundRect(r, radius))
            {
                if (fill.A > 0)
                {
                    using (SolidBrush b = new SolidBrush(fill)) g.FillPath(b, path);
                }
                if (border.A > 0)
                {
                    using (Pen pen = new Pen(border)) g.DrawPath(pen, path);
                }
            }
            g.SmoothingMode = old;
        }

        public static void DrawCheckMark(Graphics g, Rectangle box, Color color, float width)
        {
            SmoothingMode old = g.SmoothingMode;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (Pen pen = new Pen(color, width))
            {
                pen.StartCap = LineCap.Round;
                pen.EndCap = LineCap.Round;
                pen.LineJoin = LineJoin.Round;
                g.DrawLines(pen, new PointF[] {
                    new PointF(box.X + box.Width * 0.25f, box.Y + box.Height * 0.52f),
                    new PointF(box.X + box.Width * 0.43f, box.Y + box.Height * 0.70f),
                    new PointF(box.X + box.Width * 0.76f, box.Y + box.Height * 0.33f)
                });
            }
            g.SmoothingMode = old;
        }

        // Dark / light native title bar (Windows 10 1809+ / Windows 11)
        public static void ApplyWindowFrame(Form form)
        {
            if (form == null || !form.IsHandleCreated) return;
            try
            {
                int useDark = Current.IsDark ? 1 : 0;
                if (DwmSetWindowAttribute(form.Handle, 20, ref useDark, sizeof(int)) != 0)
                {
                    DwmSetWindowAttribute(form.Handle, 19, ref useDark, sizeof(int));
                }
                SetWindowPos(form.Handle, IntPtr.Zero, 0, 0, 0, 0,
                    SWP_NOMOVE | SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE | SWP_FRAMECHANGED);
            }
            catch { }
        }

        // Dark / light native scroll bars
        public static void ApplyScrollTheme(Control c)
        {
            if (c == null || !c.IsHandleCreated) return;
            try
            {
                SetWindowTheme(c.Handle, Current.IsDark ? "DarkMode_Explorer" : "Explorer", null);
            }
            catch { }
        }

        public static void ApplyThemeTree(Control root)
        {
            foreach (Control c in root.Controls)
            {
                IThemed t = c as IThemed;
                if (t != null) t.ApplyTheme();
                ApplyThemeTree(c);
            }
        }

        public static void StyleMenu(ToolStrip menu)
        {
            if (menu == null) return;
            if (_menuRenderer == null) _menuRenderer = new ThemedMenuRenderer();
            menu.Renderer = _menuRenderer;
            menu.BackColor = Current.CardBg;
            menu.ForeColor = Current.Text;
            menu.Font = GetFont(9.5f, false);
            menu.Invalidate();
        }
    }

    // Context menu / tray menu colors that follow UiTheme.Current
    internal sealed class ThemedColorTable : ProfessionalColorTable
    {
        private static UiPalette P { get { return UiTheme.Current; } }
        public override Color ToolStripDropDownBackground { get { return P.CardBg; } }
        public override Color ImageMarginGradientBegin { get { return P.CardBg; } }
        public override Color ImageMarginGradientMiddle { get { return P.CardBg; } }
        public override Color ImageMarginGradientEnd { get { return P.CardBg; } }
        public override Color MenuBorder { get { return P.CardBorder; } }
        public override Color MenuItemBorder { get { return P.SelectedRowBg; } }
        public override Color MenuItemSelected { get { return P.SelectedRowBg; } }
        public override Color MenuItemSelectedGradientBegin { get { return P.SelectedRowBg; } }
        public override Color MenuItemSelectedGradientEnd { get { return P.SelectedRowBg; } }
        public override Color MenuItemPressedGradientBegin { get { return P.SelectedRowBg; } }
        public override Color MenuItemPressedGradientMiddle { get { return P.SelectedRowBg; } }
        public override Color MenuItemPressedGradientEnd { get { return P.SelectedRowBg; } }
        public override Color SeparatorDark { get { return P.CardBorder; } }
        public override Color SeparatorLight { get { return P.CardBg; } }
        public override Color CheckBackground { get { return P.AccentChipBg; } }
        public override Color CheckSelectedBackground { get { return P.AccentChipBg; } }
        public override Color CheckPressedBackground { get { return P.AccentChipBg; } }
    }

    internal sealed class ThemedMenuRenderer : ToolStripProfessionalRenderer
    {
        public ThemedMenuRenderer() : base(new ThemedColorTable())
        {
            RoundedEdges = false;
        }

        protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
        {
            Color c = e.Item.Enabled ? UiTheme.Current.Text : UiTheme.Current.TextMuted;
            TextRenderer.DrawText(e.Graphics, e.Text, e.TextFont, e.TextRectangle, c, e.TextFormat);
        }

        protected override void OnRenderArrow(ToolStripArrowRenderEventArgs e)
        {
            e.ArrowColor = UiTheme.Current.Text;
            base.OnRenderArrow(e);
        }

        protected override void OnRenderItemCheck(ToolStripItemImageRenderEventArgs e)
        {
            Rectangle r = e.ImageRectangle;
            if (r.Width <= 0 || r.Height <= 0) return;
            UiTheme.FillRound(e.Graphics, r, UiTheme.S(3), UiTheme.Current.AccentChipBg, Color.Empty);
            UiTheme.DrawCheckMark(e.Graphics, r, UiTheme.Current.Accent, Math.Max(1.5f, UiTheme.S(2)));
        }
    }

    // =========================================================================
    // Custom-painted controls (rounded, themed)
    // =========================================================================
    internal class CardPanel : Panel, IThemed
    {
        public CardPanel()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.ResizeRedraw | ControlStyles.UserPaint, true);
            ApplyTheme();
        }

        public void ApplyTheme()
        {
            BackColor = UiTheme.Current.CardBg;
            Invalidate();
        }

        protected override void OnPaintBackground(PaintEventArgs e)
        {
            UiPalette p = UiTheme.Current;
            e.Graphics.Clear(Parent != null ? Parent.BackColor : p.WindowBg);
            UiTheme.FillRound(e.Graphics, new Rectangle(0, 0, Width - 1, Height - 1), UiTheme.S(8), p.CardBg, p.CardBorder);
        }
    }

    internal class ThemedLabel : Label, IThemed
    {
        public LabelRole Role;

        public ThemedLabel(LabelRole role, float size, bool semibold)
        {
            Role = role;
            AutoSize = false;
            AutoEllipsis = true;
            UseMnemonic = false;
            TextAlign = ContentAlignment.MiddleLeft;
            Font = UiTheme.GetFont(size, semibold);
            ApplyTheme();
        }

        public void ApplyTheme()
        {
            UiPalette p = UiTheme.Current;
            if (Role == LabelRole.Secondary) ForeColor = p.TextSecondary;
            else if (Role == LabelRole.Muted) ForeColor = p.TextMuted;
            else ForeColor = p.Text;
        }
    }

    internal class ThemedLink : LinkLabel, IThemed
    {
        public ThemedLink(string text)
        {
            Text = text;
            AutoSize = true;
            UseMnemonic = false;
            Font = UiTheme.GetFont(9f, false);
            LinkBehavior = LinkBehavior.HoverUnderline;
            ApplyTheme();
        }

        public void ApplyTheme()
        {
            UiPalette p = UiTheme.Current;
            ForeColor = p.TextSecondary; // non-link part of the text (e.g. "Tác giả: ")
            LinkColor = p.Accent;
            ActiveLinkColor = p.LinkHover;
            VisitedLinkColor = p.Accent;
        }
    }

    internal class DividerLine : Control, IThemed
    {
        public DividerLine()
        {
            TabStop = false;
            ApplyTheme();
        }

        public void ApplyTheme()
        {
            BackColor = UiTheme.Current.ControlBorder;
        }
    }

    // Small rounded pill: status chips & counters
    internal class ChipLabel : Control, IThemed
    {
        private ChipKind _kind = ChipKind.Neutral;
        public bool ShowMarker = true;

        public ChipLabel()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            TabStop = false;
            Font = UiTheme.GetFont(9f, true);
        }

        public void SetChip(string text, ChipKind kind)
        {
            if (Text != text || _kind != kind)
            {
                Text = text;
                _kind = kind;
                Size = GetIdealSize();
                Invalidate();
            }
        }

        public Size GetIdealSize()
        {
            Size t = TextRenderer.MeasureText(Text ?? "", Font, Size.Empty, TextFormatFlags.NoPadding | TextFormatFlags.SingleLine);
            int w = t.Width + UiTheme.S(ShowMarker ? 38 : 20);
            int h = Math.Max(UiTheme.S(24), t.Height + UiTheme.S(8));
            return new Size(w, h);
        }

        public void ApplyTheme()
        {
            Invalidate();
        }

        protected override void OnTextChanged(EventArgs e)
        {
            base.OnTextChanged(e);
            Invalidate();
        }

        protected override void OnPaintBackground(PaintEventArgs e) { }

        protected override void OnPaint(PaintEventArgs e)
        {
            UiPalette p = UiTheme.Current;
            Color bg = p.ChipBg, fg = p.ChipText, marker = p.ChipDot;
            if (_kind == ChipKind.Success) { bg = p.SuccessBg; fg = p.Success; marker = p.Success; }
            else if (_kind == ChipKind.Warning) { bg = p.WarningBg; fg = p.Warning; marker = p.Warning; }
            else if (_kind == ChipKind.Accent) { bg = p.AccentChipBg; fg = p.AccentChipText; marker = p.AccentChipText; }

            Graphics g = e.Graphics;
            g.Clear(Parent != null ? Parent.BackColor : p.CardBg);
            UiTheme.FillRound(g, new Rectangle(0, 0, Width - 1, Height - 1), Height / 2, bg, Color.Empty);

            int x = UiTheme.S(10);
            if (ShowMarker)
            {
                x = UiTheme.S(9);
                int ms = UiTheme.S(12);
                Font iconFont = UiTheme.GetIconFont(8f);
                if (_kind == ChipKind.Success && iconFont != null)
                {
                    TextRenderer.DrawText(g, Glyph.Accept, iconFont, new Rectangle(x, 0, ms, Height), marker,
                        TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding);
                }
                else
                {
                    int d = UiTheme.S(7);
                    SmoothingMode old = g.SmoothingMode;
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    using (SolidBrush b = new SolidBrush(marker))
                    {
                        g.FillEllipse(b, x + (ms - d) / 2, (Height - d) / 2, d, d);
                    }
                    g.SmoothingMode = old;
                }
                x += ms + UiTheme.S(6);
            }
            TextRenderer.DrawText(g, Text, Font, new Rectangle(x, 0, Math.Max(0, Width - x - UiTheme.S(6)), Height), fg,
                TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding |
                TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
        }
    }

    internal class AvatarBox : Control, IThemed
    {
        private string _letter = "?";

        public AvatarBox()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            TabStop = false;
            Font = UiTheme.GetFont(16f, true);
        }

        private Image _picture;

        public string Letter
        {
            get { return _letter; }
            set
            {
                if (value != _letter)
                {
                    _letter = value;
                    Invalidate();
                }
            }
        }

        // Profile picture; null falls back to the letter
        public Image Picture
        {
            get { return _picture; }
            set
            {
                if (value != _picture)
                {
                    _picture = value;
                    Invalidate();
                }
            }
        }

        // Round avatar: the picture clipped to a circle, or the letter on an accent circle
        public static void Draw(Graphics g, Rectangle r, Image picture, string letter, Font font)
        {
            UiPalette p = UiTheme.Current;
            SmoothingMode oldMode = g.SmoothingMode;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            if (picture != null)
            {
                using (GraphicsPath path = new GraphicsPath())
                {
                    path.AddEllipse(r);
                    Region oldClip = g.Clip;
                    g.SetClip(path, CombineMode.Intersect);
                    InterpolationMode oldInterp = g.InterpolationMode;
                    g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    g.DrawImage(picture, r);
                    g.InterpolationMode = oldInterp;
                    g.Clip = oldClip;
                    oldClip.Dispose();
                }
            }
            else
            {
                using (SolidBrush b = new SolidBrush(p.Accent))
                {
                    g.FillEllipse(b, r);
                }
                TextRenderer.DrawText(g, letter ?? "?", font, r, p.AccentText,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix);
            }
            g.SmoothingMode = oldMode;
        }

        public void ApplyTheme()
        {
            Invalidate();
        }

        protected override void OnPaintBackground(PaintEventArgs e) { }

        protected override void OnPaint(PaintEventArgs e)
        {
            UiPalette p = UiTheme.Current;
            Graphics g = e.Graphics;
            g.Clear(Parent != null ? Parent.BackColor : p.CardBg);
            Draw(g, new Rectangle(0, 0, Width - 1, Height - 1), _picture, _letter, Font);
        }
    }

    // Downloads Google profile pictures in the background and keeps them in profiles\avatars
    internal static class AvatarCache
    {
        private static readonly object _sync = new object();
        private static readonly Dictionary<string, Image> _images = new Dictionary<string, Image>();
        private static readonly HashSet<string> _pending = new HashSet<string>();

        public static string Dir;

        // Raised on a worker thread after a picture becomes available
        public static event EventHandler ImageLoaded;

        public static string KeyOf(AccountProfile acc)
        {
            if (acc == null) return null;
            if (!string.IsNullOrEmpty(acc.GoogleSub)) return acc.GoogleSub;
            return string.IsNullOrEmpty(acc.Email) ? null : acc.Email.ToLowerInvariant();
        }

        // Cached picture, or null (a background load starts the first time a key is asked for)
        public static Image Get(string key, string url)
        {
            if (string.IsNullOrEmpty(key) || string.IsNullOrEmpty(Dir)) return null;
            lock (_sync)
            {
                Image img;
                if (_images.TryGetValue(key, out img)) return img;
                if (_pending.Contains(key)) return null;
                _pending.Add(key);
            }
            ThreadPool.QueueUserWorkItem(delegate { Load(key, url); });
            return null;
        }

        private static void Load(string key, string url)
        {
            Image img = null;
            try
            {
                string safe = Regex.Replace(key, @"[^A-Za-z0-9@._-]", "_");
                string path = Path.Combine(Dir, safe + ".jpg");
                if (!File.Exists(path) && !string.IsNullOrEmpty(url))
                {
                    Directory.CreateDirectory(Dir);
                    ServicePointManager.SecurityProtocol |= (SecurityProtocolType)3072; // TLS 1.2
                    using (WebClient wc = new WebClient())
                    {
                        byte[] data = wc.DownloadData(url);
                        File.WriteAllBytes(path, data);
                    }
                }
                if (File.Exists(path))
                {
                    using (MemoryStream ms = new MemoryStream(File.ReadAllBytes(path)))
                    using (Image raw = Image.FromStream(ms))
                    {
                        img = new Bitmap(raw);
                    }
                }
            }
            catch { }

            // A failed/missing picture is remembered as null: no retry until the app restarts
            lock (_sync)
            {
                _pending.Remove(key);
                _images[key] = img;
            }
            EventHandler handler = ImageLoaded;
            if (img != null && handler != null) handler(null, EventArgs.Empty);
        }
    }

    internal class ModernButton : Button, IThemed
    {
        private ButtonKind _kind;
        private string _iconGlyph;
        private string _subText;
        private bool _hover;
        private bool _pressed;
        public bool AlignLeft;
        public string FallbackText; // shown on icon-only buttons when no icon font is installed

        public ModernButton(ButtonKind kind, string text, string glyph)
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            _kind = kind;
            _iconGlyph = glyph;
            Text = text ?? "";
            FlatStyle = FlatStyle.Flat;
            FlatAppearance.BorderSize = 0;
            UseMnemonic = false;
            Cursor = Cursors.Hand;
            Font = UiTheme.GetFont(9.75f, kind == ButtonKind.Primary || kind == ButtonKind.AccentOutline);
            Height = UiTheme.S(34);
        }

        public ButtonKind Kind
        {
            get { return _kind; }
            set { _kind = value; Invalidate(); }
        }

        public string IconGlyph
        {
            get { return _iconGlyph; }
            set { _iconGlyph = value; Invalidate(); }
        }

        public string SubText
        {
            get { return _subText; }
            set
            {
                if (_subText != value)
                {
                    _subText = value;
                    Invalidate();
                }
            }
        }

        private bool IsIconOnly
        {
            get { return string.IsNullOrEmpty(Text) && !string.IsNullOrEmpty(_iconGlyph); }
        }

        private bool IsGhostLike
        {
            get { return _kind == ButtonKind.Ghost || _kind == ButtonKind.Danger; }
        }

        private Font IconFont
        {
            get { return string.IsNullOrEmpty(_iconGlyph) ? null : UiTheme.GetIconFont(Font.SizeInPoints >= 10.5f ? 12f : 10.5f); }
        }

        public void FitWidth()
        {
            if (IsIconOnly)
            {
                Width = Height;
                return;
            }
            Font iconFont = IconFont;
            int iw = iconFont != null ? UiTheme.S(16) : 0;
            int tw = TextRenderer.MeasureText(Text, Font, Size.Empty, TextFormatFlags.NoPadding | TextFormatFlags.SingleLine).Width;
            Width = iw + (iw > 0 ? UiTheme.S(8) : 0) + tw + UiTheme.S(IsGhostLike ? 22 : 30);
        }

        public void ApplyTheme()
        {
            Invalidate();
        }

        protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { _hover = false; _pressed = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnMouseDown(MouseEventArgs e) { if (e.Button == MouseButtons.Left) { _pressed = true; Invalidate(); } base.OnMouseDown(e); }
        protected override void OnMouseUp(MouseEventArgs e) { _pressed = false; Invalidate(); base.OnMouseUp(e); }
        protected override void OnEnabledChanged(EventArgs e) { Invalidate(); base.OnEnabledChanged(e); }
        protected override void OnPaintBackground(PaintEventArgs pevent) { }

        protected override void OnPaint(PaintEventArgs e)
        {
            UiPalette p = UiTheme.Current;
            Graphics g = e.Graphics;
            g.Clear(Parent != null ? Parent.BackColor : p.WindowBg);

            Color baseFill = Color.Empty, overlay = Color.Empty, border = Color.Empty;
            Color fore = p.Text, sub = p.TextSecondary;
            Color stateOverlay = _pressed ? p.PressedOverlay : (_hover ? p.HoverOverlay : Color.Empty);

            switch (_kind)
            {
                case ButtonKind.Primary:
                    baseFill = !Enabled ? p.ControlBorder : (_pressed ? p.AccentPressed : (_hover ? p.AccentHover : p.Accent));
                    fore = Enabled ? p.AccentText : p.TextMuted;
                    sub = Enabled ? p.AccentSubText : p.TextMuted;
                    break;
                case ButtonKind.AccentOutline:
                    baseFill = p.ControlBg;
                    overlay = stateOverlay;
                    border = Enabled ? p.Accent : p.ControlBorder;
                    fore = p.Accent;
                    break;
                case ButtonKind.Outline:
                    baseFill = p.ControlBg;
                    overlay = stateOverlay;
                    border = _hover ? p.ControlBorderHover : p.ControlBorder;
                    break;
                case ButtonKind.Ghost:
                    overlay = stateOverlay;
                    break;
                case ButtonKind.Danger:
                    overlay = (_hover || _pressed) ? p.DangerOverlay : Color.Empty;
                    fore = p.Danger;
                    break;
            }
            if (!Enabled)
            {
                fore = p.TextMuted;
                overlay = Color.Empty;
            }

            Rectangle r = new Rectangle(0, 0, Width - 1, Height - 1);
            int radius = UiTheme.S(6);
            UiTheme.FillRound(g, r, radius, baseFill, Color.Empty);
            UiTheme.FillRound(g, r, radius, overlay, border);
            if (Focused && ShowFocusCues)
            {
                UiTheme.FillRound(g, new Rectangle(1, 1, Width - 3, Height - 3), radius, Color.Empty, p.Text);
            }

            TextFormatFlags baseFlags = TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix;
            Font iconFont = IconFont;

            if (IsIconOnly && iconFont == null)
            {
                TextRenderer.DrawText(g, FallbackText ?? "", Font, ClientRectangle, fore, baseFlags | TextFormatFlags.HorizontalCenter);
                return;
            }

            int padX = UiTheme.S(IsGhostLike ? 10 : 14);
            int gap = UiTheme.S(8);
            int iw = iconFont != null ? UiTheme.S(16) : 0;
            int tw = string.IsNullOrEmpty(Text) ? 0 : TextRenderer.MeasureText(g, Text, Font, Size.Empty, TextFormatFlags.NoPadding | TextFormatFlags.SingleLine).Width;
            Font subFont = UiTheme.GetFont(9.75f, false);
            int subW = 0;
            if (!string.IsNullOrEmpty(_subText))
            {
                subW = Math.Min(Width / 2, TextRenderer.MeasureText(g, _subText, subFont, Size.Empty, TextFormatFlags.NoPadding | TextFormatFlags.SingleLine).Width + UiTheme.S(2));
            }

            int contentW = iw + (iw > 0 && tw > 0 ? gap : 0) + tw;
            int x = AlignLeft ? padX + UiTheme.S(4) : (Width - contentW) / 2;
            if (iw > 0)
            {
                TextRenderer.DrawText(g, _iconGlyph, iconFont, new Rectangle(x, 0, iw, Height), fore, baseFlags | TextFormatFlags.HorizontalCenter);
                x += iw + (tw > 0 ? gap : 0);
            }
            if (tw > 0)
            {
                int maxRight = Width - padX - (subW > 0 ? subW + UiTheme.S(16) : 0);
                TextRenderer.DrawText(g, Text, Font, new Rectangle(x, 0, Math.Max(0, maxRight - x), Height), fore, baseFlags | TextFormatFlags.EndEllipsis);
            }
            if (subW > 0)
            {
                TextRenderer.DrawText(g, _subText, subFont, new Rectangle(Width - padX - UiTheme.S(4) - subW, 0, subW, Height), sub,
                    baseFlags | TextFormatFlags.Right | TextFormatFlags.EndEllipsis);
            }
        }
    }

    internal static class AppIconCache
    {
        private static readonly Dictionary<string, Image> _cache = new Dictionary<string, Image>(StringComparer.OrdinalIgnoreCase);

        public static Image GetExeIcon(string exePath, bool isIdeFallback)
        {
            string key = !string.IsNullOrEmpty(exePath) ? exePath : (isIdeFallback ? "__fallback_ide" : "__fallback_agy2");
            Image cached;
            if (_cache.TryGetValue(key, out cached)) return cached;

            Bitmap bmp = null;
            if (!string.IsNullOrEmpty(exePath) && File.Exists(exePath))
            {
                try
                {
                    using (Icon ico = Icon.ExtractAssociatedIcon(exePath))
                    {
                        if (ico != null)
                        {
                            bmp = ico.ToBitmap();
                        }
                    }
                }
                catch { }
            }

            if (bmp == null)
            {
                bmp = new Bitmap(32, 32);
                using (Graphics g = Graphics.FromImage(bmp))
                {
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    Color bg = isIdeFallback ? Color.FromArgb(92, 45, 145) : Color.FromArgb(0, 103, 192);
                    using (SolidBrush br = new SolidBrush(bg))
                    {
                        g.FillEllipse(br, 1, 1, 29, 29);
                    }
                    using (Font f = new Font("Segoe UI", 10f, FontStyle.Bold))
                    {
                        TextRenderer.DrawText(g, isIdeFallback ? "IDE" : "A2", f, new Rectangle(0, 0, 32, 32), Color.White,
                            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding);
                    }
                }
            }

            _cache[key] = bmp;
            return bmp;
        }
    }

    internal class ModernCheckBox : CheckBox, IThemed
    {
        private bool _hover;
        private Image _appIcon;
        private bool _lockedChecked;

        public ModernCheckBox(string text)
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            Text = text;
            AutoSize = false;
            UseMnemonic = false;
            Cursor = Cursors.Hand;
            Font = UiTheme.GetFont(9.75f, false);
            Height = UiTheme.S(28);
        }

        public Image AppIcon
        {
            get { return _appIcon; }
            set
            {
                if (!object.ReferenceEquals(_appIcon, value))
                {
                    _appIcon = value;
                    Invalidate();
                }
            }
        }

        // When true, the checkbox stays Checked = true, renders grayed-out, and cannot be unchecked
        public bool LockedChecked
        {
            get { return _lockedChecked; }
            set
            {
                _lockedChecked = value;
                if (_lockedChecked)
                {
                    Checked = true;
                    Cursor = Cursors.Default;
                    TabStop = false;
                }
                else
                {
                    Cursor = Cursors.Hand;
                    TabStop = true;
                }
                Invalidate();
            }
        }

        public void FitWidth()
        {
            int iconW = _appIcon != null ? UiTheme.S(18 + 6) : 0;
            Width = UiTheme.S(18 + 8 + 4) + iconW + TextRenderer.MeasureText(Text, Font, Size.Empty, TextFormatFlags.NoPadding | TextFormatFlags.SingleLine).Width;
        }

        public void ApplyTheme()
        {
            Invalidate();
        }

        protected override void OnClick(EventArgs e)
        {
            if (_lockedChecked) return;
            base.OnClick(e);
        }

        protected override void OnKeyDown(KeyEventArgs kevent)
        {
            if (_lockedChecked && kevent.KeyCode == Keys.Space)
            {
                kevent.Handled = true;
                return;
            }
            base.OnKeyDown(kevent);
        }

        protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { _hover = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnCheckedChanged(EventArgs e)
        {
            if (_lockedChecked && !Checked)
            {
                Checked = true;
                return;
            }
            Invalidate();
            base.OnCheckedChanged(e);
        }
        protected override void OnPaintBackground(PaintEventArgs pevent) { }

        protected override void OnPaint(PaintEventArgs e)
        {
            UiPalette p = UiTheme.Current;
            Graphics g = e.Graphics;
            g.Clear(Parent != null ? Parent.BackColor : p.CardBg);

            bool muted = !Enabled || _lockedChecked;
            int bs = UiTheme.S(18);
            Rectangle box = new Rectangle(0, (Height - bs) / 2, bs - 1, bs - 1);
            if (Checked)
            {
                Color fill = muted ? p.TextMuted : (_hover ? p.AccentHover : p.Accent);
                UiTheme.FillRound(g, box, UiTheme.S(4), fill, Color.Empty);
                UiTheme.DrawCheckMark(g, box, p.AccentText, Math.Max(1.5f, UiTheme.S(2)));
            }
            else
            {
                UiTheme.FillRound(g, box, UiTheme.S(4), p.ControlBg, _hover ? p.TextSecondary : p.TextMuted);
            }
            if (Focused && ShowFocusCues && !_lockedChecked)
            {
                Rectangle ring = Rectangle.Inflate(box, 2, 2);
                ring.Intersect(new Rectangle(0, 0, Width - 1, Height - 1));
                UiTheme.FillRound(g, ring, UiTheme.S(5), Color.Empty, p.Text);
            }

            int tx = bs + UiTheme.S(8);
            if (_appIcon != null)
            {
                int icoSize = UiTheme.S(18);
                Rectangle icoRect = new Rectangle(tx, (Height - icoSize) / 2, icoSize, icoSize);
                InterpolationMode oldInterp = g.InterpolationMode;
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                if (muted || !Checked)
                {
                    float alpha = _lockedChecked ? 0.55f : 0.75f;
                    using (System.Drawing.Imaging.ImageAttributes ia = new System.Drawing.Imaging.ImageAttributes())
                    {
                        System.Drawing.Imaging.ColorMatrix cm = new System.Drawing.Imaging.ColorMatrix();
                        cm.Matrix33 = alpha;
                        ia.SetColorMatrix(cm);
                        g.DrawImage(_appIcon, icoRect, 0, 0, _appIcon.Width, _appIcon.Height, GraphicsUnit.Pixel, ia);
                    }
                }
                else
                {
                    g.DrawImage(_appIcon, icoRect);
                }
                g.InterpolationMode = oldInterp;
                tx += icoSize + UiTheme.S(6);
            }

            Color textColor = (!Enabled || _lockedChecked) ? p.TextMuted : p.Text;
            TextRenderer.DrawText(g, Text, Font, new Rectangle(tx, 0, Math.Max(0, Width - tx), Height), textColor,
                TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding |
                TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
        }
    }

    // Windows 11 style segmented switch (used for Sáng / Tối / Hệ thống)
    internal class SegmentedControl : Control, IThemed
    {
        private string[] _items = new string[0];
        private int _selectedIndex = -1;
        private int _hoverIndex = -1;
        public event EventHandler SelectedIndexChanged;

        public SegmentedControl()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
            TabStop = true;
            Cursor = Cursors.Hand;
            Font = UiTheme.GetFont(9.5f, false);
            Height = UiTheme.S(30);
        }

        public string[] Items
        {
            get { return _items; }
            set { _items = value ?? new string[0]; Invalidate(); }
        }

        public int SelectedIndex
        {
            get { return _selectedIndex; }
            set
            {
                if (value != _selectedIndex)
                {
                    _selectedIndex = value;
                    Invalidate();
                    if (SelectedIndexChanged != null) SelectedIndexChanged(this, EventArgs.Empty);
                }
            }
        }

        public void ApplyTheme()
        {
            Invalidate();
        }

        // Natural width of each segment (text + padding), so long labels are never cut
        private int[] SegmentWidths()
        {
            int[] w = new int[_items.Length];
            for (int i = 0; i < _items.Length; i++)
            {
                w[i] = TextRenderer.MeasureText(_items[i], UiTheme.GetFont(9.5f, true), Size.Empty,
                    TextFormatFlags.NoPadding | TextFormatFlags.SingleLine).Width + UiTheme.S(28);
            }
            return w;
        }

        public int GetIdealWidth()
        {
            int sum = UiTheme.S(6);
            foreach (int w in SegmentWidths()) sum += w;
            return sum;
        }

        // Segments share the control width in proportion to their natural widths
        private Rectangle SegmentRect(int i)
        {
            if (_items.Length == 0) return Rectangle.Empty;
            int[] w = SegmentWidths();
            int total = 0, before = 0;
            for (int k = 0; k < w.Length; k++)
            {
                total += w[k];
                if (k < i) before += w[k];
            }
            int inset = UiTheme.S(3);
            int inner = Width - inset * 2;
            int x = inset + inner * before / total;
            int right = inset + inner * (before + w[i]) / total;
            return new Rectangle(x, inset, right - x - 1, Height - inset * 2 - 1);
        }

        private int HitTest(Point pt)
        {
            for (int i = 0; i < _items.Length; i++)
            {
                if (SegmentRect(i).Contains(pt)) return i;
            }
            return -1;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            int h = HitTest(e.Location);
            if (h != _hoverIndex) { _hoverIndex = h; Invalidate(); }
            base.OnMouseMove(e);
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            _hoverIndex = -1;
            Invalidate();
            base.OnMouseLeave(e);
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            Focus();
            int h = HitTest(e.Location);
            if (h >= 0) SelectedIndex = h;
            base.OnMouseDown(e);
        }

        protected override bool IsInputKey(Keys keyData)
        {
            if (keyData == Keys.Left || keyData == Keys.Right) return true;
            return base.IsInputKey(keyData);
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Left && _selectedIndex > 0) SelectedIndex = _selectedIndex - 1;
            else if (e.KeyCode == Keys.Right && _selectedIndex < _items.Length - 1) SelectedIndex = _selectedIndex + 1;
            base.OnKeyDown(e);
        }

        protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
        protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }
        protected override void OnPaintBackground(PaintEventArgs e) { }

        protected override void OnPaint(PaintEventArgs e)
        {
            UiPalette p = UiTheme.Current;
            Graphics g = e.Graphics;
            g.Clear(Parent != null ? Parent.BackColor : p.CardBg);
            UiTheme.FillRound(g, new Rectangle(0, 0, Width - 1, Height - 1), UiTheme.S(6), p.ControlBg,
                (Focused && ShowFocusCues) ? p.Text : p.ControlBorder);

            for (int i = 0; i < _items.Length; i++)
            {
                Rectangle r = SegmentRect(i);
                bool selected = (i == _selectedIndex);
                if (selected)
                {
                    UiTheme.FillRound(g, r, UiTheme.S(4), p.Accent, Color.Empty);
                }
                else if (i == _hoverIndex)
                {
                    UiTheme.FillRound(g, r, UiTheme.S(4), p.HoverOverlay, Color.Empty);
                }
                TextRenderer.DrawText(g, _items[i], selected ? UiTheme.GetFont(9.5f, true) : Font, r,
                    selected ? p.AccentText : p.Text,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine |
                    TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
            }
        }
    }

    internal class AccountListView : ListView
    {
        [DllImport("user32.dll")]
        private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

        private const int LVM_GETHEADER = 0x101F;
        private HeaderFillerPainter _headerPainter;

        public AccountListView()
        {
            DoubleBuffered = true;
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            IntPtr header = SendMessage(Handle, LVM_GETHEADER, IntPtr.Zero, IntPtr.Zero);
            if (header != IntPtr.Zero)
            {
                _headerPainter = new HeaderFillerPainter(this);
                _headerPainter.AssignHandle(header);
            }
        }

        protected override void OnHandleDestroyed(EventArgs e)
        {
            if (_headerPainter != null)
            {
                _headerPainter.ReleaseHandle();
                _headerPainter = null;
            }
            base.OnHandleDestroyed(e);
        }

        // The header control paints the strip right of the last column itself (always in light colors);
        // repaint that strip with the themed header color after each paint
        private sealed class HeaderFillerPainter : NativeWindow
        {
            private readonly AccountListView _owner;

            public HeaderFillerPainter(AccountListView owner)
            {
                _owner = owner;
            }

            protected override void WndProc(ref Message m)
            {
                base.WndProc(ref m);
                if (m.Msg != 0x000F) return; // WM_PAINT

                int right = 0;
                foreach (ColumnHeader col in _owner.Columns) right += col.Width;
                using (Graphics g = Graphics.FromHwnd(Handle))
                {
                    Rectangle bounds = Rectangle.Round(g.VisibleClipBounds);
                    int width = Math.Max(bounds.Width, _owner.ClientSize.Width);
                    if (right >= width) return;
                    int height = bounds.Height;
                    UiPalette p = UiTheme.Current;
                    using (SolidBrush b = new SolidBrush(p.HeaderRowBg))
                    using (Pen pen = new Pen(p.RowDivider))
                    {
                        g.FillRectangle(b, right, 0, width - right, height);
                        g.DrawLine(pen, right, 0, width, 0);
                        g.DrawLine(pen, right, height - 1, width, height - 1);
                    }
                }
            }
        }

        // The native list paints column lines below the last row; cover that strip with the card color
        protected override void WndProc(ref Message m)
        {
            base.WndProc(ref m);
            if (m.Msg == 0x000F && Items.Count > 0) // WM_PAINT
            {
                int bottom = Items[Items.Count - 1].Bounds.Bottom;
                if (bottom >= 0 && bottom < ClientSize.Height)
                {
                    using (Graphics g = CreateGraphics())
                    using (SolidBrush b = new SolidBrush(UiTheme.Current.CardBg))
                    {
                        g.FillRectangle(b, 0, bottom, ClientSize.Width, ClientSize.Height - bottom);
                    }
                }
            }
        }
    }

    // =========================================================================
    // Small Prompt Dialogs (Edit Note / Select Sessions To Export)
    // =========================================================================
    internal static class PromptDialog
    {
        private static int S(int px)
        {
            return UiTheme.S(px);
        }

        private static Form CreateDialog(string title, int width, int height)
        {
            UiPalette p = UiTheme.Current;
            Form frm = new Form();
            frm.Text = title;
            frm.AutoScaleMode = AutoScaleMode.None;
            frm.FormBorderStyle = FormBorderStyle.FixedDialog;
            frm.StartPosition = FormStartPosition.CenterParent;
            frm.MaximizeBox = false;
            frm.MinimizeBox = false;
            frm.ShowInTaskbar = false;
            frm.ShowIcon = false;
            frm.ClientSize = new Size(S(width), S(height));
            frm.Font = UiTheme.GetFont(9.75f, false);
            frm.BackColor = p.CardBg;
            frm.ForeColor = p.Text;
            frm.HandleCreated += (s, e) => UiTheme.ApplyWindowFrame(frm);
            return frm;
        }

        // Buttons are laid out from right to left
        private static Panel CreateFooter(Form frm, params ModernButton[] buttons)
        {
            Panel footer = new Panel
            {
                Dock = DockStyle.Bottom,
                Height = S(64),
                BackColor = UiTheme.Current.WindowBg
            };
            footer.Paint += (s, e) =>
            {
                using (Pen pen = new Pen(UiTheme.Current.CardBorder))
                {
                    e.Graphics.DrawLine(pen, 0, 0, footer.Width, 0);
                }
            };
            int x = frm.ClientSize.Width - S(20);
            foreach (ModernButton b in buttons)
            {
                b.Height = S(32);
                b.FitWidth();
                b.Width = Math.Max(S(96), b.Width);
                x -= b.Width;
                b.Location = new Point(x, (footer.Height - b.Height) / 2);
                x -= S(8);
                footer.Controls.Add(b);
            }
            return footer;
        }

        // Runs work on a background thread. A small "please wait" dialog with a spinner appears only
        // when the work takes longer than 300 ms, so quick operations don't flash a window.
        // Returns the exception thrown by work, or null.
        public static Exception RunBusy(IWin32Window owner, string message, MethodInvoker work)
        {
            Exception error = null;
            Thread worker = new Thread(() =>
            {
                try { work(); }
                catch (Exception ex) { error = ex; }
            });
            worker.IsBackground = true;
            worker.Start();
            if (worker.Join(300)) return error;

            using (Form frm = CreateDialog("", 340, 84))
            {
                frm.FormBorderStyle = FormBorderStyle.None;
                frm.Paint += (s, e) =>
                {
                    using (Pen pen = new Pen(UiTheme.Current.CardBorder))
                    {
                        e.Graphics.DrawRectangle(pen, 0, 0, frm.ClientSize.Width - 1, frm.ClientSize.Height - 1);
                    }
                };

                int angle = 0;
                Panel spinner = new Panel { Bounds = new Rectangle(S(24), (frm.ClientSize.Height - S(28)) / 2, S(28), S(28)) };
                spinner.Paint += (s, e) =>
                {
                    e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
                    Rectangle r = new Rectangle(S(3), S(3), spinner.Width - S(7), spinner.Height - S(7));
                    using (Pen track = new Pen(UiTheme.Current.CardBorder, S(3)))
                    using (Pen arc = new Pen(UiTheme.Current.Accent, S(3)) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                    {
                        e.Graphics.DrawEllipse(track, r);
                        e.Graphics.DrawArc(arc, r, angle, 100);
                    }
                };

                ThemedLabel lbl = new ThemedLabel(LabelRole.Primary, 9.75f, false)
                {
                    Text = message,
                    TextAlign = ContentAlignment.MiddleLeft,
                    Bounds = new Rectangle(S(66), 0, frm.ClientSize.Width - S(84), frm.ClientSize.Height)
                };
                lbl.ApplyTheme();
                frm.Controls.Add(spinner);
                frm.Controls.Add(lbl);

                bool done = false;
                System.Windows.Forms.Timer tick = new System.Windows.Forms.Timer { Interval = 30 };
                tick.Tick += (s, e) =>
                {
                    angle = (angle + 12) % 360;
                    spinner.Invalidate();
                    if (!worker.IsAlive)
                    {
                        done = true;
                        tick.Stop();
                        frm.Close();
                    }
                };
                // The dialog can only close itself (no Alt+F4 while the work is still running)
                frm.FormClosing += (s, e) => { if (!done) e.Cancel = true; };
                frm.Shown += (s, e) => tick.Start();
                frm.ShowDialog(owner);
                tick.Dispose();
            }
            return error;
        }

        public static string ShowNoteDialog(IWin32Window owner, string email, string currentNote)
        {
            using (Form frm = CreateDialog("Sửa nhãn tài khoản", 420, 210))
            {
                UiPalette p = UiTheme.Current;
                int w = frm.ClientSize.Width - S(40);

                ThemedLabel lblTitle = new ThemedLabel(LabelRole.Primary, 13f, true)
                {
                    Bounds = new Rectangle(S(20), S(14), w, S(32)),
                    Text = "Nhãn cho tài khoản"
                };

                ThemedLabel lblEmail = new ThemedLabel(LabelRole.Secondary, 9.75f, false)
                {
                    Bounds = new Rectangle(S(20), S(46), w, S(20)),
                    Text = email
                };

                ThemedLabel lblField = new ThemedLabel(LabelRole.Primary, 9f, true)
                {
                    Bounds = new Rectangle(S(20), S(78), w, S(18)),
                    Text = "Ghi chú / Nhãn"
                };

                TextBox txt = new TextBox
                {
                    Bounds = new Rectangle(S(20), S(100), w, S(28)),
                    Text = currentNote ?? "",
                    BorderStyle = BorderStyle.FixedSingle,
                    BackColor = p.InputBg,
                    ForeColor = p.Text,
                    Font = UiTheme.GetFont(10f, false)
                };

                ModernButton btnOk = new ModernButton(ButtonKind.Primary, "Lưu", null) { DialogResult = DialogResult.OK };
                ModernButton btnCancel = new ModernButton(ButtonKind.Outline, "Hủy", null) { DialogResult = DialogResult.Cancel };
                Panel footer = CreateFooter(frm, btnCancel, btnOk);

                frm.Controls.AddRange(new Control[] { lblTitle, lblEmail, lblField, txt, footer });
                frm.AcceptButton = btnOk;
                frm.CancelButton = btnCancel;
                frm.Shown += (s, e) => { txt.Focus(); txt.SelectAll(); };

                if (frm.ShowDialog(owner) == DialogResult.OK)
                {
                    return txt.Text.Trim();
                }
                return null;
            }
        }

        public static List<int> ShowExportSelectionDialog(IWin32Window owner, List<AccountProfile> accounts)
        {
            using (Form frm = CreateDialog("Xuất Session", 500, 440))
            {
                int W = frm.ClientSize.Width;

                ThemedLabel lblTitle = new ThemedLabel(LabelRole.Primary, 13f, true)
                {
                    Bounds = new Rectangle(S(20), S(14), W - S(40), S(32)),
                    Text = "Chọn tài khoản muốn xuất"
                };

                ThemedLabel lblCount = new ThemedLabel(LabelRole.Secondary, 9.75f, false)
                {
                    Bounds = new Rectangle(S(20), S(50), S(240), S(24))
                };

                ThemedLink lnkClearAll = new ThemedLink("Bỏ chọn hết");
                ThemedLink lnkSelectAll = new ThemedLink("Chọn tất cả");
                Size szClear = lnkClearAll.PreferredSize;
                Size szSelect = lnkSelectAll.PreferredSize;
                lnkClearAll.Location = new Point(W - S(20) - szClear.Width, S(50) + (S(24) - szClear.Height) / 2);
                lnkSelectAll.Location = new Point(lnkClearAll.Left - S(16) - szSelect.Width, lnkClearAll.Top);

                CardPanel card = new CardPanel { Bounds = new Rectangle(S(20), S(82), W - S(40), S(270)) };
                Panel scroll = new Panel
                {
                    AutoScroll = true,
                    Bounds = new Rectangle(S(1), S(4), card.Width - S(2), card.Height - S(8))
                };
                scroll.HandleCreated += (s, e) => UiTheme.ApplyScrollTheme(scroll);
                card.Controls.Add(scroll);

                List<ModernCheckBox> boxes = new List<ModernCheckBox>();
                int rowH = S(36);
                int rowW = scroll.Width - SystemInformation.VerticalScrollBarWidth - S(16);
                for (int i = 0; i < accounts.Count; i++)
                {
                    AccountProfile a = accounts[i];
                    string itemText = string.Format("#{0}   {1}{2}{3}",
                        i + 1,
                        a.Email,
                        !string.IsNullOrEmpty(a.DisplayName) ? ("  ·  " + a.DisplayName) : "",
                        !string.IsNullOrEmpty(a.Note) ? ("  ·  " + a.Note) : "");
                    ModernCheckBox chk = new ModernCheckBox(itemText)
                    {
                        Checked = true,
                        Bounds = new Rectangle(S(12), i * rowH, rowW, rowH)
                    };
                    boxes.Add(chk);
                    scroll.Controls.Add(chk);
                }

                Action updateCount = () =>
                {
                    int n = 0;
                    foreach (ModernCheckBox b in boxes) if (b.Checked) n++;
                    lblCount.Text = string.Format("Đã chọn {0} / {1} tài khoản", n, boxes.Count);
                };
                foreach (ModernCheckBox chk in boxes)
                {
                    chk.CheckedChanged += (s, e) => updateCount();
                }
                lnkSelectAll.LinkClicked += (s, e) => { foreach (ModernCheckBox b in boxes) b.Checked = true; };
                lnkClearAll.LinkClicked += (s, e) => { foreach (ModernCheckBox b in boxes) b.Checked = false; };
                updateCount();

                ModernButton btnExport = new ModernButton(ButtonKind.Primary, "Xuất ra file…", Glyph.Upload);
                btnExport.Click += (s, e) =>
                {
                    bool any = false;
                    foreach (ModernCheckBox b in boxes) if (b.Checked) { any = true; break; }
                    if (!any)
                    {
                        MessageBox.Show(frm, "Bạn chưa tích chọn tài khoản nào để xuất!", "Thông báo", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                        return;
                    }
                    frm.DialogResult = DialogResult.OK;
                };
                ModernButton btnCancel = new ModernButton(ButtonKind.Outline, "Hủy", null) { DialogResult = DialogResult.Cancel };
                Panel footer = CreateFooter(frm, btnCancel, btnExport);

                frm.Controls.AddRange(new Control[] { lblTitle, lblCount, lnkSelectAll, lnkClearAll, card, footer });
                frm.AcceptButton = btnExport;
                frm.CancelButton = btnCancel;

                if (frm.ShowDialog(owner) == DialogResult.OK)
                {
                    List<int> selected = new List<int>();
                    for (int i = 0; i < boxes.Count; i++)
                    {
                        if (boxes[i].Checked) selected.Add(i);
                    }
                    return selected;
                }
                return null;
            }
        }
    }

    // =========================================================================
    // Main Window + System Tray
    // =========================================================================
    public class MainForm : Form
    {
        // A second launch posts Program.WM_SHOW_RUNNING_INSTANCE: show this window, even from the tray
        protected override void WndProc(ref Message m)
        {
            if (m.Msg == Program.WM_SHOW_RUNNING_INSTANCE && Program.WM_SHOW_RUNNING_INSTANCE != 0)
            {
                ShowMainWindow();
                return;
            }
            base.WndProc(ref m);
        }

        private SwitcherCore _core;
        private NotifyIcon _trayIcon;
        private ContextMenuStrip _trayMenu;
        private ContextMenuStrip _listContextMenu;
        private System.Windows.Forms.Timer _refreshTimer;
        private ToolTip _toolTip;

        // UI Controls (updated by RefreshStatusUI)
        private ChipLabel _lblInstallStatus;
        private ChipLabel _lblAppRunningStatus;
        private ThemedLabel _lblActiveAccount;
        private ThemedLabel _lblActiveSubInfo;
        private AvatarBox _avatar;
        private AccountProfile _activeProfile;

        private ModernButton _btnQuickCycle;
        private AccountListView _lvAccounts;
        private ChipLabel _lblAccountCount;
        private ThemedLabel _lblEmptyList;
        private ModernButton _btnSwitchSelected;
        private ModernButton _btnSaveCurrent;
        private ModernButton _btnExportSessions;
        private ModernButton _btnImportSessions;
        private ModernButton _btnEditNote;
        private ModernButton _btnMoveUp;
        private ModernButton _btnMoveDown;
        private ModernButton _btnDeleteSelected;

        private ModernCheckBox _chkTargetAgy2;
        private ModernCheckBox _chkTargetIde;
        private ModernCheckBox _chkConfirmClose;
        private ModernCheckBox _chkAutoRelaunch;
        private ModernCheckBox _chkMinimizeToTray;
        private SegmentedControl _segTheme;
        private TextBox _txtLogs;
        private ThemedLabel _lblLastLog;

        // Layout containers & static captions
        private CardPanel _pnlHeader;
        private CardPanel _pnlList;
        private CardPanel _pnlOptions;
        private CardPanel _pnlLogs;
        private Panel _pnlToolbar;
        private Panel _pnlLogInner;
        private ThemedLabel _lblActiveCaption;
        private ThemedLabel _lblListTitle;
        private ThemedLabel _lblListHint;
        private ThemedLabel _lblTargetsTitle;
        private ThemedLabel _lblOptionsTitle;
        private ThemedLabel _lblThemeTitle;
        private ThemedLabel _lblLogsTitle;
        private ThemedLink _lnkLocateExe;
        private ContextMenuStrip _locateExeMenu;
        private Panel _pnlFooter;
        private ThemedLink _lnkOpenFolder;
        private ThemedLink _lnkAuthor;
        private DividerLine _toolbarDivider;
        private ModernButton _btnToggleLogs;
        private ModernButton _btnRefresh;
        private ModernButton _btnCopyDiag;
        private ModernButton _btnErrorLogs;
        private ContextMenuStrip _errorLogMenu;
        private int _errorLogCount = -1;

        private bool _allowExit = false;
        private bool _hasShownTrayBalloon = false;
        private bool _logsExpanded = false;
        private bool _inLayout = false;
        private bool _inAdjustColumns = false;
        private int _activeIdx = -1;
        private int _activeIdeIdx = -1;
        private string _lastLogSignature = null;
        private string _lastTraySignature = null;

        // Order matches the segments: Sáng / Tối / Hệ thống
        private static readonly string[] ThemeModes = new string[] { "Light", "Dark", "System" };
        private const string AuthorUrl = "https://www.google.com/search?q=binhake&ie=UTF-8";

        // Minimum widths of the list columns: #, Trạng thái, Email, Tên tài khoản, Nhãn, Phiên bản, Lưu lúc (96-DPI px)
        private static int ListAvatarSize { get { return S(24); } }
        private static readonly int[] ColumnMinWidths = new int[] { 36, 90, 150, 90, 70, 70, 130 };
        private int[] _colDesired;

        private const int LVM_GETHEADER = 0x101F;

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT
        {
            public int Left, Top, Right, Bottom;
        }

        [DllImport("user32.dll")]
        private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);

        public MainForm(SwitcherCore core)
        {
            _core = core;
            UiTheme.ApplyMode(_core.Settings.UiThemeMode);
            AvatarCache.Dir = Path.Combine(_core.ProfilesDir, "avatars");
            AvatarCache.ImageLoaded += OnAvatarLoaded;
            InitializeGui();
            InitializeTray();
            ApplyTheme();
            RefreshStatusUI();

            SystemEvents.UserPreferenceChanged += OnSystemThemeChanged;

            _refreshTimer = new System.Windows.Forms.Timer();
            _refreshTimer.Interval = 4000;
            _refreshTimer.Tick += (s, e) => RefreshStatusUI();
            _refreshTimer.Start();

            // Find the Antigravity OAuth client in the background (only needed for IDE, only until it is cached)
            ThreadPool.QueueUserWorkItem(delegate { _core.PrewarmOAuthClientDiscovery(); });
        }

        private static int S(int px)
        {
            return UiTheme.S(px);
        }

        private Icon CreateAppIcon()
        {
            Bitmap bmp = new Bitmap(64, 64);
            using (Graphics g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                using (SolidBrush br = new SolidBrush(Color.FromArgb(0, 103, 192)))
                {
                    g.FillEllipse(br, 2, 2, 60, 60);
                }
                using (Pen pen = new Pen(Color.White, 5f))
                {
                    pen.StartCap = LineCap.Round;
                    pen.EndCap = LineCap.Round;
                    pen.LineJoin = LineJoin.Round;
                    // Two opposite arrows (⇄)
                    g.DrawLine(pen, 18, 25, 45, 25);
                    g.DrawLines(pen, new Point[] { new Point(37, 17), new Point(45, 25), new Point(37, 33) });
                    g.DrawLine(pen, 46, 40, 19, 40);
                    g.DrawLines(pen, new Point[] { new Point(27, 32), new Point(19, 40), new Point(27, 48) });
                }
            }
            return Icon.FromHandle(bmp.GetHicon());
        }

        private void InitializeGui()
        {
            this.SuspendLayout();
            this.Text = "Antigravity Account Switcher";
            this.AutoScaleMode = AutoScaleMode.None;
            this.ClientSize = new Size(S(980), S(720));
            this.MinimumSize = new Size(S(840), S(660));
            this.StartPosition = FormStartPosition.CenterScreen;
            this.Font = UiTheme.GetFont(9.75f, false);
            this.Icon = CreateAppIcon();

            _toolTip = new ToolTip { AutoPopDelay = 15000, InitialDelay = 400, ReshowDelay = 100 };

            // 1. Header Card (Active Account + Environment)
            _pnlHeader = new CardPanel();
            _avatar = new AvatarBox();
            _lblActiveCaption = new ThemedLabel(LabelRole.Secondary, 8.25f, true) { Text = "TÀI KHOẢN ĐANG DÙNG" };
            _lblActiveAccount = new ThemedLabel(LabelRole.Primary, 14f, true) { Text = "..." };
            _lblActiveSubInfo = new ThemedLabel(LabelRole.Secondary, 9f, false) { Text = "..." };
            _lblInstallStatus = new ChipLabel();
            _lblAppRunningStatus = new ChipLabel();

            _pnlHeader.Controls.AddRange(new Control[] {
                _avatar, _lblActiveCaption, _lblActiveAccount, _lblActiveSubInfo,
                _lblInstallStatus, _lblAppRunningStatus
            });

            // 2. Quick Cycle Button
            _btnQuickCycle = new ModernButton(ButtonKind.Primary, "Đổi sang tài khoản kế tiếp", Glyph.Switch)
            {
                AlignLeft = true,
                Font = UiTheme.GetFont(10.5f, true)
            };
            _btnQuickCycle.Click += OnQuickCycleClick;
            _toolTip.SetToolTip(_btnQuickCycle, "Đổi nhanh sang tài khoản kế tiếp trong danh sách (1-Click Next Account)");

            // 3. Accounts List Card
            _pnlList = new CardPanel();
            _lblListTitle = new ThemedLabel(LabelRole.Primary, 10.5f, true) { Text = "Tài khoản đã lưu" };
            _lblAccountCount = new ChipLabel { ShowMarker = false };
            _lblListHint = new ThemedLabel(LabelRole.Secondary, 9f, false)
            {
                Text = "Nhấp đúp để chuyển  ·  Chuột phải để xem thêm",
                TextAlign = ContentAlignment.MiddleRight
            };
            _lblEmptyList = new ThemedLabel(LabelRole.Secondary, 9.75f, false)
            {
                Text = "Chưa có tài khoản nào. Hãy đăng nhập trên Antigravity rồi bấm 'Lưu TK đang đăng nhập'.",
                TextAlign = ContentAlignment.MiddleCenter,
                Visible = false
            };

            _lvAccounts = new AccountListView
            {
                View = View.Details,
                FullRowSelect = true,
                GridLines = false,
                HideSelection = false,
                MultiSelect = false,
                OwnerDraw = true,
                BorderStyle = BorderStyle.None,
                HeaderStyle = ColumnHeaderStyle.Nonclickable,
                Font = UiTheme.GetFont(9.75f, false)
            };
            // Row height trick: an image list with the desired height
            _lvAccounts.SmallImageList = new ImageList { ImageSize = new Size(1, S(38)) };
            _lvAccounts.Columns.Add("#", S(44), HorizontalAlignment.Center);
            _lvAccounts.Columns.Add("Trạng thái", S(110), HorizontalAlignment.Left);
            _lvAccounts.Columns.Add("Email", S(220), HorizontalAlignment.Left);
            _lvAccounts.Columns.Add("Tên tài khoản", S(150), HorizontalAlignment.Left);
            _lvAccounts.Columns.Add("Nhãn", S(100), HorizontalAlignment.Left);
            _lvAccounts.Columns.Add("Phiên bản", S(90), HorizontalAlignment.Left);
            _lvAccounts.Columns.Add("Lưu lúc", S(150), HorizontalAlignment.Left);

            _lvAccounts.DoubleClick += (s, e) => OnSwitchSelectedClick(s, e);
            _lvAccounts.DrawColumnHeader += OnListDrawColumnHeader;
            _lvAccounts.DrawItem += (s, e) => { e.DrawDefault = false; };
            _lvAccounts.DrawSubItem += OnListDrawSubItem;
            _lvAccounts.SelectedIndexChanged += (s, e) => _lvAccounts.Invalidate();
            _lvAccounts.MouseMove += (s, e) =>
            {
                ListViewItem hit = _lvAccounts.GetItemAt(e.X, e.Y);
                if (hit != null) _lvAccounts.Invalidate(hit.Bounds);
            };
            _lvAccounts.ClientSizeChanged += (s, e) => AdjustColumns();
            _lvAccounts.HandleCreated += (s, e) => UiTheme.ApplyScrollTheme(_lvAccounts);

            // Context menu for ListView
            _listContextMenu = new ContextMenuStrip();
            ToolStripMenuItem mSwitch = new ToolStripMenuItem("Áp dụng tài khoản này", null, OnSwitchSelectedClick) { Font = UiTheme.GetFont(9.5f, true) };
            ToolStripMenuItem mNote = new ToolStripMenuItem("Sửa tag...", null, OnEditNoteClick);
            ToolStripMenuItem mUp = new ToolStripMenuItem("Đẩy lên trên", null, OnMoveUpClick);
            ToolStripMenuItem mDown = new ToolStripMenuItem("Đẩy xuống dưới", null, OnMoveDownClick);
            ToolStripMenuItem mExport = new ToolStripMenuItem("Xuất Session (chọn lọc)...", null, OnExportSessionsClick);
            ToolStripMenuItem mImport = new ToolStripMenuItem("Nhập Session từ file...", null, OnImportSessionsClick);
            ToolStripMenuItem mDel = new ToolStripMenuItem("Xóa tài khoản khỏi danh sách", null, OnDeleteSelectedClick);
            _listContextMenu.Items.AddRange(new ToolStripItem[] { mSwitch, new ToolStripSeparator(), mImport, mExport, new ToolStripSeparator(), mNote, mUp, mDown, new ToolStripSeparator(), mDel });
            _lvAccounts.ContextMenuStrip = _listContextMenu;

            _pnlList.Controls.AddRange(new Control[] { _lblEmptyList, _lblListTitle, _lblAccountCount, _lblListHint, _lvAccounts });

            // 4. Action Toolbar
            _pnlToolbar = new Panel();

            _btnSwitchSelected = new ModernButton(ButtonKind.Primary, "Áp dụng TK đang chọn", Glyph.Switch);
            _btnSwitchSelected.Click += OnSwitchSelectedClick;
            _toolTip.SetToolTip(_btnSwitchSelected, "Đăng nhập tài khoản đang chọn vào các ứng dụng đã tích ở mục Áp dụng");

            _btnSaveCurrent = new ModernButton(ButtonKind.Outline, "Lưu TK đang đăng nhập", Glyph.Add);
            _btnSaveCurrent.Click += OnSaveCurrentAccountClick;
            _toolTip.SetToolTip(_btnSaveCurrent, "Lưu / Cập nhật tài khoản đang đăng nhập trên Antigravity vào danh sách");

            _toolbarDivider = new DividerLine();

            _btnExportSessions = new ModernButton(ButtonKind.Ghost, "Xuất", Glyph.Upload) { AccessibleName = "Xuất" };
            _btnExportSessions.Click += OnExportSessionsClick;
            _toolTip.SetToolTip(_btnExportSessions, "Xuất Session (chọn lọc) ra file .json");

            _btnImportSessions = new ModernButton(ButtonKind.Ghost, "Nhập", Glyph.Download) { AccessibleName = "Nhập" };
            _btnImportSessions.Click += OnImportSessionsClick;
            _toolTip.SetToolTip(_btnImportSessions, "Nhập Session từ file .json");

            _btnEditNote = new ModernButton(ButtonKind.Ghost, "Sửa tag", Glyph.Edit) { AccessibleName = "Sửa tag" };
            _btnEditNote.Click += OnEditNoteClick;
            _toolTip.SetToolTip(_btnEditNote, "Sửa tag (nhãn) cho tài khoản đang chọn");

            _btnMoveUp = new ModernButton(ButtonKind.Ghost, "", Glyph.Up) { FallbackText = "↑", AccessibleName = "Đẩy lên" };
            _btnMoveUp.Click += OnMoveUpClick;
            _toolTip.SetToolTip(_btnMoveUp, "Đẩy lên trên");

            _btnMoveDown = new ModernButton(ButtonKind.Ghost, "", Glyph.Down) { FallbackText = "↓", AccessibleName = "Đẩy xuống" };
            _btnMoveDown.Click += OnMoveDownClick;
            _toolTip.SetToolTip(_btnMoveDown, "Đẩy xuống dưới");

            _btnDeleteSelected = new ModernButton(ButtonKind.Danger, "Xóa tài khoản", Glyph.Delete) { AccessibleName = "Xóa tài khoản" };
            _btnDeleteSelected.Click += OnDeleteSelectedClick;
            _toolTip.SetToolTip(_btnDeleteSelected, "Xóa tài khoản đang chọn khỏi danh sách đã lưu");

            _pnlToolbar.Controls.AddRange(new Control[] {
                _btnSwitchSelected, _btnSaveCurrent, _toolbarDivider, _btnImportSessions, _btnExportSessions,
                _btnEditNote, _btnMoveUp, _btnMoveDown, _btnDeleteSelected
            });

            // 5. Options Card
            _pnlOptions = new CardPanel();
            _lblTargetsTitle = new ThemedLabel(LabelRole.Primary, 9.75f, true) { Text = "Áp dụng" };

            EnvironmentStatus initEnv = _core.InspectEnvironment();
            _chkTargetAgy2 = new ModernCheckBox("Antigravity 2.0")
            {
                Checked = true,
                LockedChecked = true,
                AppIcon = AppIconCache.GetExeIcon(initEnv.ExePath, false)
            };
            _toolTip.SetToolTip(_chkTargetAgy2, "Antigravity 2.0 luôn được áp dụng mặc định (không thể bỏ tích)");

            _chkTargetIde = new ModernCheckBox("Antigravity IDE")
            {
                Checked = _core.Settings.ApplyToIde,
                LockedChecked = false,
                AppIcon = AppIconCache.GetExeIcon(initEnv.IdeExePath, true)
            };
            _chkTargetIde.CheckedChanged += (s, e) =>
            {
                _chkTargetIde.Refresh();
                _core.Settings.ApplyToIde = _chkTargetIde.Checked;
                _core.SaveSettings();
                _core.Log("Đã " + (_chkTargetIde.Checked ? "BẬT" : "TẮT") + " áp dụng cho Antigravity IDE.");
            };
            _toolTip.SetToolTip(_chkTargetIde, "Tích chọn để đổi / lưu tài khoản đồng thời cho cả Antigravity IDE");

            // One place to locate both executables
            _locateExeMenu = new ContextMenuStrip();
            _locateExeMenu.Items.Add(new ToolStripMenuItem("Antigravity 2.0  (Antigravity.exe)…", null, OnLocateExeClick));
            _locateExeMenu.Items.Add(new ToolStripMenuItem("Antigravity IDE  (Antigravity IDE.exe)…", null, OnLocateIdeExeClick));
            _lnkLocateExe = new ThemedLink("Định vị lại file thực thi…");
            _lnkLocateExe.LinkClicked += (s, e) => _locateExeMenu.Show(_lnkLocateExe, new Point(0, _lnkLocateExe.Height));
            _toolTip.SetToolTip(_lnkLocateExe, "Chọn đường dẫn Antigravity.exe / Antigravity IDE.exe nếu bạn cài ở thư mục khác");

            _lblOptionsTitle = new ThemedLabel(LabelRole.Primary, 9.75f, true) { Text = "Cài đặt" };

            _chkConfirmClose = new ModernCheckBox("Hỏi trước khi đóng Antigravity") { Checked = _core.Settings.ConfirmBeforeClosingApp };
            _chkConfirmClose.CheckedChanged += (s, e) => { _core.Settings.ConfirmBeforeClosingApp = _chkConfirmClose.Checked; _core.SaveSettings(); };
            _toolTip.SetToolTip(_chkConfirmClose, "Hỏi xác nhận trước khi tự động đóng ứng dụng");

            _chkAutoRelaunch = new ModernCheckBox("Tự mở lại ứng dụng sau khi chuyển TK")
            {
                Checked = _core.Settings.AutoRelaunchAfterSwitch
            };
            _chkAutoRelaunch.CheckedChanged += (s, e) => { _core.Settings.AutoRelaunchAfterSwitch = _chkAutoRelaunch.Checked; _core.SaveSettings(); };
            _toolTip.SetToolTip(_chkAutoRelaunch, "Tự động mở lại ứng dụng được tích chọn sau khi đổi tài khoản");

            _chkMinimizeToTray = new ModernCheckBox("Thu nhỏ xuống trayicon khi đóng cửa sổ") { Checked = _core.Settings.MinimizeToTrayOnClose };
            _chkMinimizeToTray.CheckedChanged += (s, e) => { _core.Settings.MinimizeToTrayOnClose = _chkMinimizeToTray.Checked; _core.SaveSettings(); };
            _toolTip.SetToolTip(_chkMinimizeToTray, "Thu nhỏ xuống Khay hệ thống (System Tray) khi bấm X");

            _lblThemeTitle = new ThemedLabel(LabelRole.Primary, 9.75f, true) { Text = "Giao diện" };
            _segTheme = new SegmentedControl { Items = new string[] { "Sáng", "Tối", "Hệ thống" } };
            _segTheme.SelectedIndex = Array.IndexOf(ThemeModes, UiTheme.Mode);
            _segTheme.SelectedIndexChanged += OnThemeModeChanged;

            _lnkOpenFolder = new ThemedLink("Mở thư mục Session");
            _lnkOpenFolder.LinkClicked += (s, e) => { try { Process.Start("explorer.exe", _core.ProfilesDir); } catch { } };

            // Author credit: only "Binhake" is clickable
            _lnkAuthor = new ThemedLink("Tác giả: Binhake ツ");
            _lnkAuthor.LinkArea = new LinkArea(9, 9);
            _lnkAuthor.LinkClicked += (s, e) => { try { Process.Start(AuthorUrl); } catch { } };
            _toolTip.SetToolTip(_lnkAuthor, AuthorUrl);

            _pnlOptions.Controls.AddRange(new Control[] {
                _lblTargetsTitle, _chkTargetAgy2, _chkTargetIde, _lnkLocateExe,
                _lblThemeTitle, _segTheme,
                _lblOptionsTitle, _chkConfirmClose, _chkAutoRelaunch, _chkMinimizeToTray
            });

            // Footer row (outside the cards): utility links on the left, author on the right
            _pnlFooter = new Panel();
            _pnlFooter.Controls.AddRange(new Control[] { _lnkOpenFolder, _lnkAuthor });

            // 6. Diagnostics & Logs Card (collapsible)
            _pnlLogs = new CardPanel();
            _btnToggleLogs = new ModernButton(ButtonKind.Ghost, "", Glyph.ChevronRight) { FallbackText = "▸", AccessibleName = "Mở / thu gọn nhật ký" };
            _btnToggleLogs.Click += (s, e) => ToggleLogs();
            _toolTip.SetToolTip(_btnToggleLogs, "Mở / thu gọn nhật ký");

            _lblLogsTitle = new ThemedLabel(LabelRole.Primary, 9.75f, true) { Text = "Nhật ký & chẩn đoán", Cursor = Cursors.Hand };
            _lblLogsTitle.Click += (s, e) => ToggleLogs();

            _lblLastLog = new ThemedLabel(LabelRole.Secondary, 9f, false) { Font = UiTheme.GetMonoFont(9f), Cursor = Cursors.Hand };
            _lblLastLog.Click += (s, e) => ToggleLogs();

            _btnRefresh = new ModernButton(ButtonKind.Ghost, "Làm mới", Glyph.Refresh) { Font = UiTheme.GetFont(9f, false) };
            _btnRefresh.Click += (s, e) => { _core.Log("Làm mới trạng thái thủ công."); RefreshStatusUI(); };
            _toolTip.SetToolTip(_btnRefresh, "Làm mới trạng thái");

            _btnCopyDiag = new ModernButton(ButtonKind.Ghost, "Copy thông tin lỗi", Glyph.Copy) { Font = UiTheme.GetFont(9f, false) };
            _btnCopyDiag.Click += OnCopyDiagnosticsClick;
            _toolTip.SetToolTip(_btnCopyDiag, "Copy thông tin lỗi & phiên bản vào Clipboard");

            // Shown only when error log files exist (logs\Error_*.txt)
            _errorLogMenu = new ContextMenuStrip();
            _errorLogMenu.Items.Add(new ToolStripMenuItem("Mở thư mục log lỗi", null, (s, e) =>
            {
                try { Process.Start("explorer.exe", _core.ErrorLogDir); }
                catch (Exception ex) { MessageBox.Show(this, "Không mở được thư mục: " + ex.Message, "Lỗi", MessageBoxButtons.OK, MessageBoxIcon.Error); }
            }));
            _errorLogMenu.Items.Add(new ToolStripSeparator());
            _errorLogMenu.Items.Add(new ToolStripMenuItem("Xóa tất cả log lỗi", null, OnClearErrorLogsClick));
            _btnErrorLogs = new ModernButton(ButtonKind.Danger, "Log lỗi", Glyph.Error) { Font = UiTheme.GetFont(9f, false), Visible = false };
            _btnErrorLogs.Click += (s, e) => _errorLogMenu.Show(_btnErrorLogs, new Point(0, _btnErrorLogs.Height));
            _toolTip.SetToolTip(_btnErrorLogs, "Mở hoặc dọn dẹp các file log lỗi");

            _pnlLogInner = new Panel { Padding = new Padding(S(10), S(8), S(4), S(8)), Visible = false };
            _txtLogs = new TextBox
            {
                Multiline = true,
                ReadOnly = true,
                ScrollBars = ScrollBars.Vertical,
                BorderStyle = BorderStyle.None,
                Dock = DockStyle.Fill,
                Font = UiTheme.GetMonoFont(9f)
            };
            _txtLogs.HandleCreated += (s, e) => UiTheme.ApplyScrollTheme(_txtLogs);
            _pnlLogInner.Controls.Add(_txtLogs);

            _pnlLogs.Controls.AddRange(new Control[] {
                _btnToggleLogs, _lblLogsTitle, _lblLastLog, _btnErrorLogs, _btnRefresh, _btnCopyDiag, _pnlLogInner
            });

            this.Controls.AddRange(new Control[] {
                _pnlHeader, _btnQuickCycle, _pnlList, _pnlToolbar, _pnlOptions, _pnlLogs, _pnlFooter
            });

            this.FormClosing += OnMainFormClosing;
            this.FormClosed += (s, e) => SystemEvents.UserPreferenceChanged -= OnSystemThemeChanged;
            this.ResumeLayout(false);
        }

        // ---------------------------------------------------------------------
        // Layout (manual, DPI-scaled) — the accounts list takes all spare height
        // ---------------------------------------------------------------------
        protected override void OnLayout(LayoutEventArgs levent)
        {
            base.OnLayout(levent);
            LayoutMain();
        }

        private void LayoutMain()
        {
            if (_inLayout || _pnlLogs == null) return;
            _inLayout = true;
            try
            {
                int pad = S(16), gap = S(12);
                int w = ClientSize.Width - pad * 2;
                if (w < S(200) || ClientSize.Height < S(200)) return;

                int y = S(12);
                _pnlHeader.SetBounds(pad, y, w, S(88));
                y += S(88) + gap;
                _btnQuickCycle.SetBounds(pad, y, w, S(46));
                y += S(46) + gap;

                int footH = S(22);
                _pnlFooter.SetBounds(pad, ClientSize.Height - S(8) - footH, w, footH);
                int bottom = ClientSize.Height - S(8) - footH - S(6);
                int logsH = _logsExpanded ? S(230) : S(44);
                _pnlLogs.SetBounds(pad, bottom - logsH, w, logsH);
                bottom -= logsH + gap;

                int optH = ArrangeOptions(w, false);
                _pnlOptions.SetBounds(pad, bottom - optH, w, optH);
                bottom -= optH + gap;

                int tbH = S(34);
                _pnlToolbar.SetBounds(pad, bottom - tbH, w, tbH);
                bottom -= tbH + gap;

                _pnlList.SetBounds(pad, y, w, Math.Max(S(120), bottom - y));

                LayoutHeader();
                LayoutListCard();
                LayoutToolbar();
                LayoutOptions();
                LayoutLogs();
                LayoutFooter();
            }
            finally
            {
                _inLayout = false;
            }
        }

        private void LayoutHeader()
        {
            int W = _pnlHeader.Width, H = _pnlHeader.Height, p = S(18);
            int av = S(52);
            _avatar.SetBounds(p, (H - av) / 2, av, av);

            Size chip2 = _lblAppRunningStatus.GetIdealSize();
            Size chip1 = _lblInstallStatus.GetIdealSize();
            int chipY = (H - chip1.Height) / 2;
            int x2 = W - p - chip2.Width;
            _lblAppRunningStatus.SetBounds(x2, chipY, chip2.Width, chip2.Height);
            int x1 = x2 - S(6) - chip1.Width;
            _lblInstallStatus.SetBounds(x1, chipY, chip1.Width, chip1.Height);

            int tx = p + av + S(16);
            int tw = Math.Max(S(120), x1 - S(16) - tx);
            int ty = (H - S(62)) / 2;
            _lblActiveCaption.SetBounds(tx, ty, tw, S(16));
            _lblActiveAccount.SetBounds(tx, ty + S(16), tw, S(28));
            _lblActiveSubInfo.SetBounds(tx, ty + S(44), tw, S(18));
        }

        private void LayoutListCard()
        {
            int W = _pnlList.Width, H = _pnlList.Height, p = S(16);
            int headerH = S(46);
            Size title = _lblListTitle.PreferredSize;
            _lblListTitle.SetBounds(p, (headerH - title.Height) / 2, title.Width, title.Height);
            Size cnt = _lblAccountCount.GetIdealSize();
            _lblAccountCount.SetBounds(_lblListTitle.Right + S(8), (headerH - cnt.Height) / 2, cnt.Width, cnt.Height);
            int hintX = _lblAccountCount.Right + S(16);
            _lblListHint.SetBounds(hintX, 0, Math.Max(0, W - p - hintX), headerH);

            // List is only as tall as its rows (scrolls when they don't fit), the card shows below it
            int maxListH = Math.Max(S(40), H - headerH - S(6));
            _lvAccounts.SetBounds(1, headerH, W - 2, Math.Min(maxListH, GetListContentHeight()));
            _lblEmptyList.SetBounds(S(16), headerH + S(40), W - S(32), S(60));
            AdjustColumns();
        }

        private int GetListContentHeight()
        {
            int rowH = S(38);
            if (_lvAccounts.IsHandleCreated && _lvAccounts.Items.Count > 0)
            {
                int h = _lvAccounts.Items[0].Bounds.Height;
                if (h > 0) rowH = h;
            }
            return GetListHeaderHeight() + rowH * _lvAccounts.Items.Count + S(2);
        }

        private int GetListHeaderHeight()
        {
            if (_lvAccounts.IsHandleCreated)
            {
                IntPtr header = SendMessage(_lvAccounts.Handle, LVM_GETHEADER, IntPtr.Zero, IntPtr.Zero);
                RECT rc;
                if (header != IntPtr.Zero && GetWindowRect(header, out rc) && rc.Bottom > rc.Top)
                {
                    return rc.Bottom - rc.Top;
                }
            }
            return S(26);
        }

        // Measure the widest text of every column (header + all rows) — called after the list is filled
        private void MeasureColumns()
        {
            int n = _lvAccounts.Columns.Count;
            int[] desired = new int[n];
            TextFormatFlags mf = TextFormatFlags.NoPadding | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix;
            Font headerFont = UiTheme.GetFont(9f, true);
            Font rowFont = UiTheme.GetFont(9.75f, false);
            Font rowBoldFont = UiTheme.GetFont(9.75f, true);

            for (int c = 0; c < n; c++)
            {
                desired[c] = TextRenderer.MeasureText(_lvAccounts.Columns[c].Text, headerFont, Size.Empty, mf).Width;
            }
            foreach (ListViewItem item in _lvAccounts.Items)
            {
                int idx = (item.Tag is int) ? (int)item.Tag : item.Index;
                bool isCurrent = (idx == _activeIdx) || (idx == _activeIdeIdx);
                for (int c = 0; c < n && c < item.SubItems.Count; c++)
                {
                    string text = item.SubItems[c].Text;
                    if (c == 4 && string.IsNullOrEmpty(text)) text = "—";
                    int w = TextRenderer.MeasureText(text, (isCurrent && c < 5) ? rowBoldFont : rowFont, Size.Empty, mf).Width;
                    if (c == 2) w += ListAvatarSize + S(8);
                    if (w > desired[c]) desired[c] = w;
                }
            }
            for (int c = 0; c < n; c++)
            {
                desired[c] = Math.Max(S(ColumnMinWidths[c]), desired[c] + S(22));
            }
            _colDesired = desired;
        }

        // Columns fit their content; spare width is shared by the text columns (2..6),
        // and when the window is too narrow the text columns shrink toward their minimum
        private void AdjustColumns()
        {
            if (_inAdjustColumns) return;
            if (_lvAccounts == null || _lvAccounts.Columns.Count < ColumnMinWidths.Length) return;
            if (_colDesired == null || _colDesired.Length != _lvAccounts.Columns.Count) MeasureColumns();

            _inAdjustColumns = true;
            try
            {
                int n = _colDesired.Length;
                int[] widths = (int[])_colDesired.Clone();
                int avail = Math.Max(0, _lvAccounts.ClientSize.Width - S(4));
                int total = 0;
                foreach (int w in widths) total += w;

                if (total < avail)
                {
                    int extra = avail - total, weightSum = 0, given = 0;
                    for (int c = 2; c < n; c++) weightSum += widths[c];
                    for (int c = 2; c < n; c++)
                    {
                        int add = (c == n - 1) ? (extra - given) : (extra * widths[c] / Math.Max(1, weightSum));
                        widths[c] += add;
                        given += add;
                    }
                }
                else if (total > avail)
                {
                    int over = total - avail, slackSum = 0, taken = 0;
                    int[] slack = new int[n];
                    for (int c = 2; c < n; c++)
                    {
                        slack[c] = Math.Max(0, widths[c] - S(ColumnMinWidths[c]));
                        slackSum += slack[c];
                    }
                    int cut = Math.Min(over, slackSum);
                    for (int c = 2; c < n && slackSum > 0; c++)
                    {
                        int d = cut * slack[c] / slackSum;
                        widths[c] -= d;
                        taken += d;
                    }
                    // Rounding leftovers
                    for (int c = 2; c < n && taken < cut; c++)
                    {
                        int d = Math.Min(widths[c] - S(ColumnMinWidths[c]), cut - taken);
                        if (d > 0) { widths[c] -= d; taken += d; }
                    }
                }

                bool anyChange = false;
                for (int c = 0; c < n; c++)
                {
                    if (_lvAccounts.Columns[c].Width != widths[c]) { anyChange = true; break; }
                }
                if (anyChange)
                {
                    _lvAccounts.BeginUpdate();
                    try
                    {
                        // Shrink columns first so total width never momentarily exceeds ClientSize.Width
                        // (which would otherwise flash a horizontal/vertical scrollbar and re-enter ClientSizeChanged)
                        for (int c = 0; c < n; c++)
                        {
                            if (widths[c] < _lvAccounts.Columns[c].Width) _lvAccounts.Columns[c].Width = widths[c];
                        }
                        for (int c = 0; c < n; c++)
                        {
                            if (widths[c] > _lvAccounts.Columns[c].Width) _lvAccounts.Columns[c].Width = widths[c];
                        }
                    }
                    finally
                    {
                        _lvAccounts.EndUpdate();
                    }
                }
            }
            finally
            {
                _inAdjustColumns = false;
            }
        }

        private void LayoutToolbar()
        {
            // Full labels first; when the row doesn't fit, the secondary buttons become icon-only (tooltips stay)
            ArrangeToolbar(false);
            if (_btnDeleteSelected.Left < _btnMoveDown.Right + S(8)) ArrangeToolbar(true);
        }

        private void ArrangeToolbar(bool compact)
        {
            foreach (ModernButton b in new ModernButton[] { _btnImportSessions, _btnExportSessions, _btnEditNote, _btnDeleteSelected })
            {
                string label = compact ? "" : b.AccessibleName;
                if (b.Text != label) b.Text = label;
            }
            int W = _pnlToolbar.Width, H = _pnlToolbar.Height;
            int x = 0;
            foreach (ModernButton b in new ModernButton[] { _btnSwitchSelected, _btnSaveCurrent })
            {
                b.Height = H;
                b.FitWidth();
                b.Location = new Point(x, 0);
                x += b.Width + S(6);
            }
            x += S(4);
            _toolbarDivider.SetBounds(x, (H - S(20)) / 2, Math.Max(1, S(1)), S(20));
            x += Math.Max(1, S(1)) + S(10);
            foreach (ModernButton b in new ModernButton[] { _btnImportSessions, _btnExportSessions, _btnEditNote, _btnMoveUp, _btnMoveDown })
            {
                b.Height = H;
                b.FitWidth();
                b.Location = new Point(x, 0);
                x += b.Width + S(2);
            }
            _btnDeleteSelected.Height = H;
            _btnDeleteSelected.FitWidth();
            _btnDeleteSelected.Location = new Point(W - _btnDeleteSelected.Width, 0);
        }

        private void LayoutOptions()
        {
            ArrangeOptions(_pnlOptions.Width, true);
        }

        // Checkboxes and links wrap to a new row when the card is too narrow.
        // Returns the card height needed for width W (apply = false only measures).
        private int ArrangeOptions(int W, bool apply)
        {
            int p = S(16), rowH = S(28), rowStep = S(34);
            int labelW = S(86), left = p + labelW, right = W - p;
            int y = S(12);

            // Row 1: Target Apps (Antigravity 2.0 [locked] + Antigravity IDE [toggleable]) + exe picker on the right
            if (apply) _lblTargetsTitle.SetBounds(p, y, labelW, rowH);
            int tx = left;
            foreach (ModernCheckBox tc in new ModernCheckBox[] { _chkTargetAgy2, _chkTargetIde })
            {
                tc.Height = rowH;
                tc.FitWidth();
                if (tx > left && tx + tc.Width > right)
                {
                    tx = left;
                    y += rowStep;
                }
                if (apply) tc.Location = new Point(tx, y);
                tx += tc.Width + S(24);
            }
            Size exeLnkSz = _lnkLocateExe.PreferredSize;
            if (tx + exeLnkSz.Width > right) y += rowStep;
            if (apply) _lnkLocateExe.SetBounds(right - exeLnkSz.Width, y + (rowH - exeLnkSz.Height) / 2, exeLnkSz.Width, exeLnkSz.Height);

            // Row 2: Theme segmented switch
            y += rowStep;
            if (apply)
            {
                _lblThemeTitle.SetBounds(p, y, labelW, rowH);
                _segTheme.SetBounds(left, y - S(1), _segTheme.GetIdealWidth(), S(30));
            }

            // Row 3: General settings checkboxes
            y += rowStep;
            if (apply) _lblOptionsTitle.SetBounds(p, y, labelW, rowH);
            int x = left;
            foreach (ModernCheckBox c in new ModernCheckBox[] { _chkConfirmClose, _chkAutoRelaunch, _chkMinimizeToTray })
            {
                c.Height = rowH;
                c.FitWidth();
                if (x > left && x + c.Width > right)
                {
                    x = left;
                    y += rowStep;
                }
                if (apply) c.Location = new Point(x, y);
                x += c.Width + S(22);
            }
            return y + rowH + S(12);
        }

        // Footer: utility links on the left, author credit on the right
        private void LayoutFooter()
        {
            int W = _pnlFooter.Width, H = _pnlFooter.Height, x = S(2);
            foreach (ThemedLink l in new ThemedLink[] { _lnkOpenFolder })
            {
                Size sz = l.PreferredSize;
                l.SetBounds(x, (H - sz.Height) / 2, sz.Width, sz.Height);
                x += sz.Width + S(20);
            }
            Size au = _lnkAuthor.PreferredSize;
            _lnkAuthor.SetBounds(Math.Max(x, W - S(2) - au.Width), (H - au.Height) / 2, au.Width, au.Height);
        }

        private void LayoutLogs()
        {
            int W = _pnlLogs.Width, H = _pnlLogs.Height;
            int headH = S(44), bh = S(30), by = (headH - bh) / 2;

            _btnToggleLogs.Height = bh;
            _btnToggleLogs.FitWidth();
            _btnToggleLogs.Location = new Point(S(6), by);

            Size title = _lblLogsTitle.PreferredSize;
            _lblLogsTitle.SetBounds(_btnToggleLogs.Right + S(2), 0, title.Width, headH);

            _btnCopyDiag.Height = bh;
            _btnCopyDiag.FitWidth();
            _btnCopyDiag.Location = new Point(W - S(6) - _btnCopyDiag.Width, by);
            _btnRefresh.Height = bh;
            _btnRefresh.FitWidth();
            _btnRefresh.Location = new Point(_btnCopyDiag.Left - S(2) - _btnRefresh.Width, by);
            int buttonsLeft = _btnRefresh.Left;
            if (_btnErrorLogs.Visible)
            {
                _btnErrorLogs.Height = bh;
                _btnErrorLogs.FitWidth();
                _btnErrorLogs.Location = new Point(_btnRefresh.Left - S(2) - _btnErrorLogs.Width, by);
                buttonsLeft = _btnErrorLogs.Left;
            }

            int lx = _lblLogsTitle.Right + S(12);
            _lblLastLog.SetBounds(lx, 0, Math.Max(0, buttonsLeft - S(12) - lx), headH);
            _lblLastLog.Visible = !_logsExpanded;

            _pnlLogInner.Visible = _logsExpanded;
            if (_logsExpanded)
            {
                _pnlLogInner.SetBounds(S(12), headH, W - S(24), Math.Max(S(40), H - headH - S(12)));
            }
        }

        private void ToggleLogs()
        {
            _logsExpanded = !_logsExpanded;
            _btnToggleLogs.IconGlyph = _logsExpanded ? Glyph.ChevronDown : Glyph.ChevronRight;
            _btnToggleLogs.FallbackText = _logsExpanded ? "▾" : "▸";
            LayoutMain();
            if (_logsExpanded)
            {
                _txtLogs.SelectionStart = _txtLogs.Text.Length;
                _txtLogs.ScrollToCaret();
            }
        }

        // ---------------------------------------------------------------------
        // Theme (Light / Dark / System)
        // ---------------------------------------------------------------------
        private void ApplyTheme()
        {
            UiPalette p = UiTheme.Current;
            this.BackColor = p.WindowBg;
            this.ForeColor = p.Text;
            UiTheme.ApplyThemeTree(this);

            _lvAccounts.BackColor = p.CardBg;
            _lvAccounts.ForeColor = p.Text;
            _pnlLogInner.BackColor = p.LogBg;
            _txtLogs.BackColor = p.LogBg;
            _txtLogs.ForeColor = p.LogText;

            UiTheme.StyleMenu(_listContextMenu);
            UiTheme.StyleMenu(_trayMenu);
            UiTheme.StyleMenu(_locateExeMenu);
            UiTheme.StyleMenu(_errorLogMenu);
            UiTheme.ApplyWindowFrame(this);
            UiTheme.ApplyScrollTheme(_lvAccounts);
            UiTheme.ApplyScrollTheme(_txtLogs);
            this.Invalidate(true);
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            UiTheme.ApplyWindowFrame(this);
        }

        private void OnThemeModeChanged(object sender, EventArgs e)
        {
            int idx = _segTheme.SelectedIndex;
            if (idx < 0 || idx >= ThemeModes.Length) return;
            string mode = ThemeModes[idx];
            _core.Settings.UiThemeMode = mode;
            _core.SaveSettings();
            UiTheme.ApplyMode(mode);
            _core.Log("Đã đổi giao diện sang: " + _segTheme.Items[idx] + ".");
            ApplyTheme();
            RefreshStatusUI();
        }

        private void OnSystemThemeChanged(object sender, UserPreferenceChangedEventArgs e)
        {
            if (UiTheme.Mode != "System" || this.IsDisposed || !this.IsHandleCreated) return;
            this.BeginInvoke(new MethodInvoker(delegate
            {
                if (UiTheme.ApplyMode("System"))
                {
                    ApplyTheme();
                    RefreshStatusUI();
                }
            }));
        }

        // ---------------------------------------------------------------------
        // Owner-drawn accounts list
        // ---------------------------------------------------------------------
        // A name equal to the email is the parser's fallback, not a real account name
        private static bool HasCustomName(AccountProfile acc)
        {
            return !string.IsNullOrEmpty(acc.DisplayName) &&
                   !string.Equals(acc.DisplayName, acc.Email, StringComparison.OrdinalIgnoreCase);
        }

        // "44.4.3 (Engine: 1.2.3)" -> "v44.4.3"
        private static string ShortVersion(string version)
        {
            if (string.IsNullOrEmpty(version)) return "";
            int cut = version.IndexOf(" (", StringComparison.Ordinal);
            string v = cut > 0 ? version.Substring(0, cut) : version;
            return (v.Length > 0 && char.IsDigit(v[0])) ? ("v" + v) : v;
        }

        private void OnListDrawColumnHeader(object sender, DrawListViewColumnHeaderEventArgs e)
        {
            UiPalette p = UiTheme.Current;
            Rectangle r = e.Bounds;
            using (SolidBrush b = new SolidBrush(p.HeaderRowBg))
            {
                e.Graphics.FillRectangle(b, r);
            }
            using (Pen pen = new Pen(p.RowDivider))
            {
                e.Graphics.DrawLine(pen, r.Left, r.Top, r.Right, r.Top);
                e.Graphics.DrawLine(pen, r.Left, r.Bottom - 1, r.Right, r.Bottom - 1);
            }
            TextFormatFlags flags = TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine |
                                    TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix;
            Rectangle tr = r;
            if (e.ColumnIndex == 0) flags |= TextFormatFlags.HorizontalCenter;
            else tr = new Rectangle(r.X + S(10), r.Y, Math.Max(0, r.Width - S(14)), r.Height);
            TextRenderer.DrawText(e.Graphics, e.Header.Text, UiTheme.GetFont(9f, true), tr, p.TextSecondary, flags);
        }

        private void OnListDrawSubItem(object sender, DrawListViewSubItemEventArgs e)
        {
            UiPalette p = UiTheme.Current;
            Graphics g = e.Graphics;
            Rectangle r = e.Bounds;
            int col = e.ColumnIndex;
            // Sub-item 0 reports the whole row as its bounds
            if (col == 0) r = new Rectangle(r.X, r.Y, _lvAccounts.Columns[0].Width, r.Height);

            int idx = (e.Item.Tag is int) ? (int)e.Item.Tag : e.ItemIndex;
            bool isCurrentAgy2 = (idx == _activeIdx);
            bool isCurrentIde = (idx == _activeIdeIdx);
            bool isCurrent = isCurrentAgy2 || isCurrentIde;
            bool selected = e.Item.Selected;

            Color bg = selected ? p.SelectedRowBg : (isCurrent ? p.ActiveRowBg : p.CardBg);
            using (SolidBrush b = new SolidBrush(bg))
            {
                g.FillRectangle(b, r);
            }
            using (Pen pen = new Pen(p.RowDivider))
            {
                g.DrawLine(pen, r.Left, r.Bottom - 1, r.Right, r.Bottom - 1);
            }

            string text = e.SubItem != null ? e.SubItem.Text : "";
            Color fc = p.Text;
            if (col == 0 || col == 1 || col >= 5) fc = p.TextSecondary;
            if (col == 0 && isCurrent) fc = p.Text;
            if (col == 1 && isCurrent) fc = p.Success;
            if (col == 3 && idx >= 0 && idx < _core.Accounts.Count && !HasCustomName(_core.Accounts[idx])) fc = p.TextMuted;
            if (col == 4 && string.IsNullOrEmpty(text))
            {
                text = "—";
                fc = p.TextMuted;
            }
            Font f = (isCurrent && col < 5) ? UiTheme.GetFont(9.75f, true) : UiTheme.GetFont(9.75f, false);

            TextFormatFlags flags = TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine |
                                    TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix;
            Rectangle tr = r;
            if (col == 0) flags |= TextFormatFlags.HorizontalCenter;
            else tr = new Rectangle(r.X + S(10), r.Y, Math.Max(0, r.Width - S(16)), r.Height);

            // Email column: round avatar on the left
            if (col == 2 && idx >= 0 && idx < _core.Accounts.Count && r.Width > ListAvatarSize + S(16))
            {
                AccountProfile acc = _core.Accounts[idx];
                int a = ListAvatarSize;
                Rectangle ar = new Rectangle(r.X + S(10), r.Y + (r.Height - a) / 2, a, a);
                Image pic = AvatarCache.Get(AvatarCache.KeyOf(acc), _core.GetPictureUrl(acc));
                AvatarBox.Draw(g, ar, pic, GetAvatarLetter(acc.Email), UiTheme.GetFont(8.25f, true));
                tr = new Rectangle(ar.Right + S(8), r.Y, Math.Max(0, r.Right - S(6) - ar.Right - S(8)), r.Height);
            }
            TextRenderer.DrawText(g, text, f, tr, fc, flags);
        }

        // ---------------------------------------------------------------------
        // System Tray
        // ---------------------------------------------------------------------
        private void InitializeTray()
        {
            _trayMenu = new ContextMenuStrip();
            _trayIcon = new NotifyIcon
            {
                Icon = this.Icon,
                Text = "Antigravity Account Switcher",
                Visible = true,
                ContextMenuStrip = _trayMenu
            };
            _trayIcon.DoubleClick += (s, e) => ShowMainWindow();
        }

        private void RebuildTrayMenu(EnvironmentStatus env, AccountProfile active, int activeIdx)
        {
            _trayMenu.Items.Clear();

            string activeLabel = active != null ? ("Đang dùng: " + active.Email) : "Chưa đăng nhập";
            ToolStripMenuItem itemActive = new ToolStripMenuItem(activeLabel) { Enabled = false };
            string verLabel = env.IdeIsInstalled
                ? string.Format("Antigravity 2.0 (v{0}) + IDE (v{1})", env.Version, env.IdeVersion)
                : ("Antigravity 2.0: v" + env.Version);
            ToolStripMenuItem itemVer = new ToolStripMenuItem(verLabel) { Enabled = false };
            _trayMenu.Items.Add(itemActive);
            _trayMenu.Items.Add(itemVer);
            _trayMenu.Items.Add(new ToolStripSeparator());

            ToolStripMenuItem itemQuick = new ToolStripMenuItem("Đổi nhanh sang tài khoản kế tiếp");
            itemQuick.Font = UiTheme.GetFont(9.5f, true);
            itemQuick.Enabled = (_core.Accounts.Count > 0);
            itemQuick.Click += OnQuickCycleClick;
            _trayMenu.Items.Add(itemQuick);
            _trayMenu.Items.Add(new ToolStripSeparator());

            if (_core.Accounts.Count == 0)
            {
                _trayMenu.Items.Add(new ToolStripMenuItem("(Danh sách đang trống)") { Enabled = false });
            }
            else
            {
                for (int i = 0; i < _core.Accounts.Count; i++)
                {
                    AccountProfile acc = _core.Accounts[i];
                    bool isCurr = (i == activeIdx);
                    string label = string.Format("#{0}  {1}{2}",
                        i + 1,
                        acc.Email,
                        !string.IsNullOrEmpty(acc.Note) ? ("  (" + acc.Note + ")") : "");
                    ToolStripMenuItem itemAcc = new ToolStripMenuItem(label);
                    if (isCurr)
                    {
                        itemAcc.Checked = true;
                        itemAcc.Font = UiTheme.GetFont(9.5f, true);
                    }
                    int capturedIndex = i;
                    itemAcc.Click += (s, e) => ExecuteSwitchByIndex(capturedIndex);
                    _trayMenu.Items.Add(itemAcc);
                }
            }

            _trayMenu.Items.Add(new ToolStripSeparator());

            ToolStripMenuItem itemDiag = new ToolStripMenuItem("Copy thông tin chẩn đoán / lỗi");
            itemDiag.Click += OnCopyDiagnosticsClick;
            _trayMenu.Items.Add(itemDiag);

            ToolStripMenuItem itemOpen = new ToolStripMenuItem("Mở cửa sổ quản lý");
            itemOpen.Click += (s, e) => ShowMainWindow();
            _trayMenu.Items.Add(itemOpen);

            ToolStripMenuItem itemExit = new ToolStripMenuItem("Thoát hoàn toàn");
            itemExit.Click += (s, e) =>
            {
                _allowExit = true;
                _trayIcon.Visible = false;
                Application.Exit();
            };
            _trayMenu.Items.Add(itemExit);
        }

        private void OnAvatarLoaded(object sender, EventArgs e)
        {
            try
            {
                if (IsHandleCreated && !IsDisposed) BeginInvoke((MethodInvoker)UpdateAvatars);
            }
            catch { }
        }

        private void UpdateAvatars()
        {
            _avatar.Picture = (_activeProfile != null)
                ? AvatarCache.Get(AvatarCache.KeyOf(_activeProfile), _activeProfile.PictureUrl)
                : null;
            _lvAccounts.Invalidate();
        }

        private static string GetAvatarLetter(string email)
        {
            if (string.IsNullOrEmpty(email)) return "?";
            return char.ToUpperInvariant(email[0]).ToString();
        }

        public void RefreshStatusUI()
        {
            UiPalette p = UiTheme.Current;
            EnvironmentStatus env = _core.InspectEnvironment();
            bool applyIde = _core.Settings.ApplyToIde;

            // Update App Target checkboxes & icons
            _chkTargetAgy2.AppIcon = AppIconCache.GetExeIcon(env.ExePath, false);
            _chkTargetIde.AppIcon = AppIconCache.GetExeIcon(env.IdeExePath, true);
            if (_chkTargetIde.Checked != applyIde) _chkTargetIde.Checked = applyIde;

            _toolTip.SetToolTip(_chkTargetAgy2, string.Format(
                "Antigravity 2.0 (Luôn áp dụng mặc định)\r\n• Phiên bản: v{0}\r\n• Trạng thái: {1}\r\n• Đường dẫn: {2}",
                env.Version, env.IsRunning ? "Đang chạy" : "Đã đóng", env.ExePath ?? "Chưa tìm thấy"));

            string ideErr, ideRef, ideAcc;
            long ideExp;
            AccountProfile ideActive = _core.ReadCurrentIdeAccount(env, out ideErr, out ideRef, out ideAcc, out ideExp);
            int ideActiveIdx = ideActive != null ? _core.FindAccountIndexByEmailOrSub(ideActive.Email, ideActive.GoogleSub) : -1;
            int prevIdeActiveIdx = _activeIdeIdx;
            _activeIdeIdx = ideActiveIdx;

            _toolTip.SetToolTip(_chkTargetIde, string.Format(
                "Antigravity IDE ({0})\r\n• Phiên bản: v{1}\r\n• Trạng thái: {2}\r\n• Tài khoản đang dùng trên IDE: {3}\r\n• Đường dẫn: {4}",
                applyIde ? "ĐANG ÁP DỤNG" : "Tích chọn để áp dụng thêm cho IDE",
                env.IdeVersion,
                env.IdeIsRunning ? "Đang chạy" : "Đã đóng",
                ideActive != null ? ideActive.Email : "Chưa đăng nhập",
                env.IdeExePath ?? "Chưa tìm thấy"));

            if (env.IsInstalled)
            {
                string verChip = env.IdeIsInstalled
                    ? string.Format("AG 2.0 v{0} · IDE v{1}", ShortVersion(env.Version).TrimStart('v'), ShortVersion(env.IdeVersion).TrimStart('v'))
                    : ("Antigravity v" + env.Version);
                _lblInstallStatus.SetChip(verChip, ChipKind.Success);
                _toolTip.SetToolTip(_lblInstallStatus, string.Format(
                    "Antigravity 2.0: v{0} ({1})\r\nAntigravity IDE: v{2} ({3})",
                    env.Version, env.ExePath ?? "Chưa xác định",
                    env.IdeVersion, env.IdeExePath ?? "Chưa xác định"));
            }
            else
            {
                _lblInstallStatus.SetChip("Chưa tìm thấy Antigravity.exe", ChipKind.Warning);
                _toolTip.SetToolTip(_lblInstallStatus, "Chưa tìm thấy Antigravity.exe (Hãy bấm 'Định vị lại file thực thi…' ở mục Áp dụng nếu cài ở nơi khác)");
            }

            bool anyRunning = env.IsRunning || env.IdeIsRunning;
            if (anyRunning)
            {
                string runChip = (env.IsRunning && env.IdeIsRunning)
                    ? "Đang chạy (2.0 + IDE)"
                    : (env.IdeIsRunning ? "Đang chạy (IDE)" : "Đang chạy");
                _lblAppRunningStatus.SetChip(runChip, ChipKind.Accent);
                _toolTip.SetToolTip(_lblAppRunningStatus, string.Format(
                    "Tiến trình đang chạy:\r\n• Antigravity 2.0 PID: [{0}] (language_server: [{1}])\r\n• Antigravity IDE PID: [{2}] (language_server_x64: [{3}])",
                    string.Join(",", env.AntigravityPids), string.Join(",", env.LanguageServerPids),
                    string.Join(",", env.IdePids), string.Join(",", env.IdeLanguageServerPids)));
            }
            else
            {
                _lblAppRunningStatus.SetChip("Đã đóng · sẵn sàng đổi", ChipKind.Neutral);
                _toolTip.SetToolTip(_lblAppRunningStatus, string.Format("Đã đóng hoàn toàn (Sẵn sàng chuyển đổi ngay)\r\nĐường dẫn: {0}",
                    env.ExePath ?? "Chưa xác định"));
            }

            string activeErr;
            AccountProfile active = _core.ReadCurrentActiveAccount(env, out activeErr);
            int activeIdx = -1;

            if (active != null)
            {
                activeIdx = _core.FindAccountIndexByEmailOrSub(active.Email, active.GoogleSub);
                _avatar.Letter = GetAvatarLetter(active.Email);
                _avatar.Picture = AvatarCache.Get(AvatarCache.KeyOf(active), active.PictureUrl);
                _lblActiveAccount.Text = active.Email;
                _lblActiveAccount.ForeColor = p.Text;
                if (ideActive != null && !string.Equals(ideActive.Email, active.Email, StringComparison.OrdinalIgnoreCase))
                {
                    _lblActiveSubInfo.Text = string.Format("AG 2.0: {0}  ·  IDE đang dùng: {1}",
                        active.DisplayName, ideActive.Email);
                }
                else
                {
                    _lblActiveSubInfo.Text = string.Format("{0}  ·  UID {1}{2}",
                        active.DisplayName, active.GoogleSub ?? "N/A",
                        (ideActive != null) ? "  ·  Đồng bộ cả IDE" : "");
                }
            }
            else
            {
                _avatar.Letter = "?";
                _avatar.Picture = null;
                _lblActiveAccount.Text = "Chưa đăng nhập";
                _lblActiveAccount.ForeColor = p.Danger;
                _lblActiveSubInfo.Text = (string.IsNullOrEmpty(activeErr) ? "" : (activeErr + " — ")) +
                    "Chọn 1 tài khoản bên dưới rồi bấm 'Áp dụng TK đang chọn'.";
            }
            _toolTip.SetToolTip(_lblActiveSubInfo, _lblActiveSubInfo.Text);
            int prevActiveIdx = _activeIdx;
            _activeIdx = activeIdx;
            _activeProfile = active;

            // Update ListView items in-place when count matches to avoid full clear/repaint jitter
            int selectedIndex = _lvAccounts.SelectedIndices.Count > 0 ? _lvAccounts.SelectedIndices[0] : -1;
            bool listChanged = (prevActiveIdx != activeIdx) || (prevIdeActiveIdx != ideActiveIdx);

            if (_lvAccounts.Items.Count != _core.Accounts.Count)
            {
                listChanged = true;
                _lvAccounts.BeginUpdate();
                try
                {
                    _lvAccounts.Items.Clear();
                    for (int i = 0; i < _core.Accounts.Count; i++)
                    {
                        AccountProfile acc = _core.Accounts[i];
                        bool isCurrAgy2 = (i == activeIdx);
                        bool isCurrIde = (i == ideActiveIdx);
                        string statusText = "Sẵn sàng";
                        if (ideActiveIdx < 0)
                        {
                            statusText = isCurrAgy2 ? "Đang dùng" : "Sẵn sàng";
                        }
                        else
                        {
                            if (isCurrAgy2 && isCurrIde) statusText = "Đang dùng (2.0 + IDE)";
                            else if (isCurrAgy2) statusText = "Đang dùng (2.0)";
                            else if (isCurrIde) statusText = "Đang dùng (IDE)";
                        }

                        ListViewItem item = new ListViewItem((i + 1).ToString());
                        item.SubItems.Add(statusText);
                        item.SubItems.Add(acc.Email ?? "");
                        item.SubItems.Add(HasCustomName(acc) ? acc.DisplayName : "Chưa đặt");
                        item.SubItems.Add(acc.Note ?? "");
                        item.SubItems.Add(ShortVersion(acc.SavedAntigravityVersion));
                        item.SubItems.Add(acc.SavedAt ?? "");
                        item.Tag = i;
                        _lvAccounts.Items.Add(item);
                    }

                    if (selectedIndex >= 0 && selectedIndex < _lvAccounts.Items.Count)
                    {
                        _lvAccounts.Items[selectedIndex].Selected = true;
                    }
                    else if (_lvAccounts.Items.Count > 0)
                    {
                        int defaultSelect = (activeIdx >= 0 && _core.Accounts.Count > 1)
                            ? ((activeIdx + 1) % _core.Accounts.Count)
                            : 0;
                        _lvAccounts.Items[defaultSelect].Selected = true;
                    }
                }
                finally
                {
                    _lvAccounts.EndUpdate();
                }
            }
            else
            {
                for (int i = 0; i < _core.Accounts.Count; i++)
                {
                    AccountProfile acc = _core.Accounts[i];
                    bool isCurrAgy2 = (i == activeIdx);
                    bool isCurrIde = (i == ideActiveIdx);
                    string statusText = "Sẵn sàng";
                    if (ideActiveIdx < 0)
                    {
                        statusText = isCurrAgy2 ? "Đang dùng" : "Sẵn sàng";
                    }
                    else
                    {
                        if (isCurrAgy2 && isCurrIde) statusText = "Đang dùng (2.0 + IDE)";
                        else if (isCurrAgy2) statusText = "Đang dùng (2.0)";
                        else if (isCurrIde) statusText = "Đang dùng (IDE)";
                    }

                    string[] cells = new string[]
                    {
                        (i + 1).ToString(),
                        statusText,
                        acc.Email ?? "",
                        HasCustomName(acc) ? acc.DisplayName : "Chưa đặt",
                        acc.Note ?? "",
                        ShortVersion(acc.SavedAntigravityVersion),
                        acc.SavedAt ?? ""
                    };

                    ListViewItem item = _lvAccounts.Items[i];
                    item.Tag = i;
                    for (int c = 0; c < cells.Length && c < item.SubItems.Count; c++)
                    {
                        if (item.SubItems[c].Text != cells[c])
                        {
                            item.SubItems[c].Text = cells[c];
                            listChanged = true;
                        }
                    }
                }
            }

            if (listChanged)
            {
                MeasureColumns();
                _lvAccounts.Invalidate();
            }

            _lblAccountCount.SetChip(_core.Accounts.Count.ToString(), ChipKind.Neutral);
            _lblEmptyList.Visible = (_core.Accounts.Count == 0);

            // Update Quick Cycle button text
            if (_core.Accounts.Count == 0)
            {
                _btnQuickCycle.Text = "Đổi sang tài khoản kế tiếp";
                _btnQuickCycle.SubText = "Danh sách đang trống";
                _btnQuickCycle.Enabled = false;
            }
            else if (_core.Accounts.Count == 1)
            {
                _btnQuickCycle.Text = "Nạp tài khoản #1";
                _btnQuickCycle.SubText = _core.Accounts[0].Email + "  ·  lưu thêm TK để xoay vòng";
                _btnQuickCycle.Enabled = true;
            }
            else
            {
                int nextIdx = (activeIdx >= 0) ? ((activeIdx + 1) % _core.Accounts.Count) : 0;
                AccountProfile nextAcc = _core.Accounts[nextIdx];
                _btnQuickCycle.Text = "Đổi sang tài khoản kế tiếp";
                _btnQuickCycle.SubText = string.Format("#{0}  ·  {1}", nextIdx + 1, nextAcc.Email);
                _btnQuickCycle.Enabled = true;
            }

            // Update Tray tooltip & menu (only rebuild menu when state changes)
            string tooltip = string.Format("Antigravity Switcher ({0} TK)\nActive: {1}",
                _core.Accounts.Count, active != null ? active.Email : "None");
            if (tooltip.Length > 63) tooltip = tooltip.Substring(0, 63);
            if (_trayIcon.Text != tooltip) _trayIcon.Text = tooltip;

            string traySig = string.Format("{0}|{1}|{2}|{3}|{4}",
                _core.Accounts.Count, activeIdx, ideActiveIdx, env.Version, env.IdeVersion);
            if (traySig != _lastTraySignature)
            {
                _lastTraySignature = traySig;
                RebuildTrayMenu(env, active, activeIdx);
            }

            // Update logs (only when something new was logged, so manual scrolling is kept)
            string[] logs = _core.GetActivityLogSnapshot();
            string lastLine = logs.Length > 0 ? logs[logs.Length - 1] : "";
            string signature = logs.Length + "|" + lastLine;
            if (signature != _lastLogSignature)
            {
                _lastLogSignature = signature;
                _txtLogs.Lines = logs;
                _txtLogs.SelectionStart = _txtLogs.Text.Length;
                _txtLogs.ScrollToCaret();
                _lblLastLog.Text = lastLine;
                _toolTip.SetToolTip(_lblLastLog, lastLine);
            }
            UpdateErrorLogButton();

            LayoutMain();
        }

        private int GetSelectedAccountIndex()
        {
            if (_lvAccounts.SelectedIndices.Count == 0) return -1;
            return _lvAccounts.SelectedIndices[0];
        }

        private void ExecuteSwitchByIndex(int index)
        {
            string msg;
            bool ok = _core.SwitchToAccountIndex(index, this, out msg);
            RefreshStatusUI();
            if (!ok && msg != "Đã hủy thao tác chuyển đổi theo yêu cầu của bạn.")
            {
                DialogResult dr = MessageBox.Show(this,
                    msg + "\n\nBạn có muốn Copy thông tin lỗi kèm phiên bản Antigravity vào Clipboard để kiểm tra không?",
                    "Lỗi chuyển đổi tài khoản",
                    MessageBoxButtons.YesNo, MessageBoxIcon.Error);
                if (dr == DialogResult.Yes)
                {
                    OnCopyDiagnosticsClick(this, EventArgs.Empty);
                }
            }
            else if (ok)
            {
                _trayIcon.ShowBalloonTip(2500, "Đã chuyển tài khoản Antigravity", msg, ToolTipIcon.Info);
            }
        }

        private void OnSwitchSelectedClick(object sender, EventArgs e)
        {
            int idx = GetSelectedAccountIndex();
            if (idx < 0)
            {
                MessageBox.Show(this, "Vui lòng chọn một tài khoản trong danh sách trước!", "Thông báo", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            ExecuteSwitchByIndex(idx);
        }

        private void OnQuickCycleClick(object sender, EventArgs e)
        {
            if (_core.Accounts.Count == 0)
            {
                MessageBox.Show(this, "Danh sách tài khoản đang trống. Hãy bấm 'Lưu TK đang đăng nhập' trước!", "Thông báo", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            string err;
            AccountProfile active = _core.ReadCurrentActiveAccount(out err);
            int activeIdx = active != null ? _core.FindAccountIndexByEmailOrSub(active.Email, active.GoogleSub) : -1;
            int nextIdx = (activeIdx >= 0) ? ((activeIdx + 1) % _core.Accounts.Count) : 0;

            ExecuteSwitchByIndex(nextIdx);
        }

        private void OnSaveCurrentAccountClick(object sender, EventArgs e)
        {
            string msg = null;
            int savedIdx = -1;
            bool ok = false;
            string busyText = _core.Settings.ApplyToIde
                ? "Đang lưu tài khoản (2.0 + IDE)…\r\nLần đầu có thể mất vài giây."
                : "Đang lưu tài khoản…";

            // The status timer reads the same data, so it pauses while the save runs in the background
            _refreshTimer.Stop();
            Exception error = PromptDialog.RunBusy(this, busyText, () => { ok = _core.SaveOrUpdateCurrentAccount(null, out msg, out savedIdx); });
            _refreshTimer.Start();
            if (error != null)
            {
                ok = false;
                msg = "Lỗi không mong muốn khi lưu tài khoản: " + error.Message;
                _core.RecordException("ERR_SAVE_EXCEPTION", error);
            }
            RefreshStatusUI();
            if (ok && savedIdx >= 0 && savedIdx < _lvAccounts.Items.Count)
            {
                _lvAccounts.SelectedIndices.Clear();
                _lvAccounts.Items[savedIdx].Selected = true;
                _lvAccounts.EnsureVisible(savedIdx);
            }
            MessageBox.Show(this, msg, ok ? "Lưu thành công" : "Lỗi khi lưu",
                MessageBoxButtons.OK, ok ? MessageBoxIcon.Information : MessageBoxIcon.Error);
        }

        private void OnEditNoteClick(object sender, EventArgs e)
        {
            int idx = GetSelectedAccountIndex();
            if (idx < 0 || idx >= _core.Accounts.Count)
            {
                MessageBox.Show(this, "Hãy chọn 1 tài khoản trong danh sách để sửa ghi chú!", "Thông báo", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            AccountProfile acc = _core.Accounts[idx];
            string newNote = PromptDialog.ShowNoteDialog(this, acc.Email, acc.Note);
            if (newNote != null)
            {
                acc.Note = newNote;
                _core.SaveAccounts();
                _core.Log(string.Format("Đã đổi ghi chú cho '{0}' thành: '{1}'.", acc.Email, newNote));
                RefreshStatusUI();
            }
        }

        private void OnMoveUpClick(object sender, EventArgs e)
        {
            int idx = GetSelectedAccountIndex();
            if (idx <= 0 || idx >= _core.Accounts.Count) return;
            AccountProfile tmp = _core.Accounts[idx - 1];
            _core.Accounts[idx - 1] = _core.Accounts[idx];
            _core.Accounts[idx] = tmp;
            _core.SaveAccounts();
            RefreshStatusUI();
            _lvAccounts.SelectedIndices.Clear();
            _lvAccounts.Items[idx - 1].Selected = true;
        }

        private void OnMoveDownClick(object sender, EventArgs e)
        {
            int idx = GetSelectedAccountIndex();
            if (idx < 0 || idx >= _core.Accounts.Count - 1) return;
            AccountProfile tmp = _core.Accounts[idx + 1];
            _core.Accounts[idx + 1] = _core.Accounts[idx];
            _core.Accounts[idx] = tmp;
            _core.SaveAccounts();
            RefreshStatusUI();
            _lvAccounts.SelectedIndices.Clear();
            _lvAccounts.Items[idx + 1].Selected = true;
        }

        private void OnDeleteSelectedClick(object sender, EventArgs e)
        {
            int idx = GetSelectedAccountIndex();
            if (idx < 0 || idx >= _core.Accounts.Count)
            {
                MessageBox.Show(this, "Hãy chọn 1 tài khoản trong danh sách để xóa!", "Thông báo", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            AccountProfile acc = _core.Accounts[idx];
            DialogResult dr = MessageBox.Show(this,
                string.Format("Bạn có chắc chắn muốn xóa tài khoản #{0} '{1}' khỏi danh sách đã lưu không?\n(Phiên đang đăng nhập trên Antigravity sẽ không bị ảnh hưởng).",
                    idx + 1, acc.Email),
                "Xác nhận xóa khỏi danh sách",
                MessageBoxButtons.YesNo, MessageBoxIcon.Warning);

            if (dr == DialogResult.Yes)
            {
                _core.Accounts.RemoveAt(idx);
                _core.SaveAccounts();
                _core.Log(string.Format("Đã xóa tài khoản '{0}' khỏi danh sách.", acc.Email));
                RefreshStatusUI();
            }
        }

        private void OnExportSessionsClick(object sender, EventArgs e)
        {
            if (_core.Accounts.Count == 0)
            {
                MessageBox.Show(this, "Danh sách hiện đang trống, chưa có Session nào để xuất!", "Thông báo", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            List<int> selectedIndices = PromptDialog.ShowExportSelectionDialog(this, _core.Accounts);
            if (selectedIndices == null || selectedIndices.Count == 0)
            {
                return;
            }

            using (SaveFileDialog sfd = new SaveFileDialog())
            {
                sfd.Title = "Chọn nơi lưu file Xuất dữ liệu Session";
                sfd.Filter = "Antigravity Session JSON (*.json)|*.json|All Files (*.*)|*.*";
                sfd.FileName = string.Format("antigravity_sessions_{0:yyyyMMdd_HHmm}.json", DateTime.Now);
                // Always start in the app's own folder instead of the last-used / Documents folder
                sfd.InitialDirectory = _core.BaseDir;
                sfd.RestoreDirectory = true;
                if (sfd.ShowDialog(this) == DialogResult.OK)
                {
                    string msg;
                    bool ok = _core.ExportSelectedAccounts(selectedIndices, sfd.FileName, out msg);
                    RefreshStatusUI();
                    MessageBox.Show(this, msg, ok ? "Xuất Session thành công" : "Lỗi Xuất Session",
                        MessageBoxButtons.OK, ok ? MessageBoxIcon.Information : MessageBoxIcon.Error);
                }
            }
        }

        private void OnImportSessionsClick(object sender, EventArgs e)
        {
            using (OpenFileDialog ofd = new OpenFileDialog())
            {
                ofd.Title = "Chọn file dữ liệu Session (.json) để nhập vào danh sách";
                ofd.Filter = "Antigravity Session JSON (*.json)|*.json|All Files (*.*)|*.*";
                if (ofd.ShowDialog(this) == DialogResult.OK)
                {
                    string msg;
                    int addedCount, updatedCount;
                    bool ok = _core.ImportAccountsFromFile(ofd.FileName, this, out msg, out addedCount, out updatedCount);
                    RefreshStatusUI();
                    MessageBox.Show(this, msg, ok ? "Nhập Session thành công" : "Lỗi Nhập Session",
                        MessageBoxButtons.OK, ok ? MessageBoxIcon.Information : MessageBoxIcon.Error);
                }
            }
        }

        private void OnLocateExeClick(object sender, EventArgs e)
        {
            using (OpenFileDialog ofd = new OpenFileDialog())
            {
                ofd.Title = "Chọn file Antigravity.exe trên máy của bạn";
                ofd.Filter = "Antigravity Executable (Antigravity.exe)|Antigravity.exe|All Executables (*.exe)|*.exe";

                // Start in the default install folder (%LOCALAPPDATA%\Programs\Antigravity) when it exists
                string defaultDir = SwitcherCore.GetDefaultAntigravityInstallDir();
                string defaultExe = SwitcherCore.GetDefaultAntigravityExePath();

                if (File.Exists(defaultExe))
                {
                    ofd.InitialDirectory = defaultDir;
                    ofd.FileName = "Antigravity.exe";
                }
                else if (Directory.Exists(defaultDir))
                {
                    ofd.InitialDirectory = defaultDir;
                }
                else if (!string.IsNullOrEmpty(_core.Settings.CustomAntigravityExePath) &&
                         Directory.Exists(Path.GetDirectoryName(_core.Settings.CustomAntigravityExePath)))
                {
                    ofd.InitialDirectory = Path.GetDirectoryName(_core.Settings.CustomAntigravityExePath);
                    ofd.FileName = Path.GetFileName(_core.Settings.CustomAntigravityExePath);
                }

                if (ofd.ShowDialog(this) == DialogResult.OK)
                {
                    _core.Settings.CustomAntigravityExePath = ofd.FileName;
                    _core.SaveSettings();
                    _core.Log("Đã cập nhật đường dẫn Antigravity.exe thủ công: " + ofd.FileName);
                    RefreshStatusUI();
                }
            }
        }

        private void OnLocateIdeExeClick(object sender, EventArgs e)
        {
            using (OpenFileDialog ofd = new OpenFileDialog())
            {
                ofd.Title = "Chọn file Antigravity IDE.exe trên máy của bạn";
                ofd.Filter = "Antigravity IDE Executable (Antigravity IDE.exe)|Antigravity IDE.exe|All Executables (*.exe)|*.exe";

                string defaultDir = SwitcherCore.GetDefaultAntigravityIdeInstallDir();
                string defaultExe = SwitcherCore.GetDefaultAntigravityIdeExePath();

                if (File.Exists(defaultExe))
                {
                    ofd.InitialDirectory = defaultDir;
                    ofd.FileName = "Antigravity IDE.exe";
                }
                else if (Directory.Exists(defaultDir))
                {
                    ofd.InitialDirectory = defaultDir;
                }
                else if (!string.IsNullOrEmpty(_core.Settings.CustomAntigravityIdeExePath) &&
                         Directory.Exists(Path.GetDirectoryName(_core.Settings.CustomAntigravityIdeExePath)))
                {
                    ofd.InitialDirectory = Path.GetDirectoryName(_core.Settings.CustomAntigravityIdeExePath);
                    ofd.FileName = Path.GetFileName(_core.Settings.CustomAntigravityIdeExePath);
                }

                if (ofd.ShowDialog(this) == DialogResult.OK)
                {
                    _core.Settings.CustomAntigravityIdeExePath = ofd.FileName;
                    _core.SaveSettings();
                    _core.Log("Đã cập nhật đường dẫn Antigravity IDE.exe thủ công: " + ofd.FileName);
                    RefreshStatusUI();
                }
            }
        }

        private void OnCopyDiagnosticsClick(object sender, EventArgs e)
        {
            try
            {
                string report = _core.BuildDiagnosticsReport();
                Clipboard.SetText(report);
                _core.Log("Đã sao chép báo cáo chẩn đoán & phiên bản vào Clipboard.");
                RefreshStatusUI();
                MessageBox.Show(this,
                    "Đã copy toàn bộ thông tin chẩn đoán (kèm Phiên bản Antigravity, Trạng thái tiến trình, Đường dẫn động và Mã lỗi) vào Clipboard!\n\nBạn có thể dán (Ctrl+V) ngay để gửi kiểm tra.",
                    "Đã Copy Thông Tin Chẩn Đoán", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "Không thể copy vào Clipboard: " + ex.Message, "Lỗi", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void OnClearErrorLogsClick(object sender, EventArgs e)
        {
            int count = _core.CountErrorLogs();
            if (count == 0) return;
            DialogResult answer = MessageBox.Show(this,
                string.Format("Xóa {0} file log lỗi trong thư mục:\r\n{1}?", count, _core.ErrorLogDir),
                "Xóa log lỗi", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
            if (answer != DialogResult.Yes) return;
            _core.ClearErrorLogs();
            RefreshStatusUI();
        }

        private void UpdateErrorLogButton()
        {
            int count = _core.CountErrorLogs();
            if (count == _errorLogCount) return;
            _errorLogCount = count;
            _btnErrorLogs.Text = string.Format("Log lỗi ({0})", count);
            _btnErrorLogs.Visible = count > 0;
            LayoutLogs();
        }

        private void ShowMainWindow()
        {
            this.Show();
            if (this.WindowState == FormWindowState.Minimized)
            {
                this.WindowState = FormWindowState.Normal;
            }
            this.BringToFront();
            this.Activate();
            RefreshStatusUI();
        }

        private void OnMainFormClosing(object sender, FormClosingEventArgs e)
        {
            if (!_allowExit && e.CloseReason == CloseReason.UserClosing && _core.Settings.MinimizeToTrayOnClose)
            {
                e.Cancel = true;
                this.Hide();
                if (!_hasShownTrayBalloon)
                {
                    _hasShownTrayBalloon = true;
                    _trayIcon.ShowBalloonTip(3000, "Antigravity Switcher đang chạy ẩn",
                        "Click chuột phải vào biểu tượng ở khay hệ thống (System Tray) để đổi nhanh tài khoản bất cứ lúc nào!",
                        ToolTipIcon.Info);
                }
            }
            else
            {
                _trayIcon.Visible = false;
            }
        }
    }

    internal static class Program
    {
        [DllImport("user32.dll")]
        private static extern bool SetProcessDPIAware();

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int RegisterWindowMessage(string message);

        [DllImport("user32.dll")]
        private static extern bool PostMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern bool AllowSetForegroundWindow(int processId);

        private static readonly IntPtr HWND_BROADCAST = new IntPtr(0xFFFF);
        private const int ASFW_ANY = -1;

        // Message the second instance broadcasts to bring the running window back
        public static readonly int WM_SHOW_RUNNING_INSTANCE = RegisterWindowMessage("AntigravityAccountSwitcher_ShowRunningInstance");

        [STAThread]
        // Command line: "--save-current" saves the signed-in account without opening the window.
        // Exit code 0 = saved, 1 = failed (details are in the app's activity log).
        static int Main(string[] args)
        {
            try { SetProcessDPIAware(); } catch (EntryPointNotFoundException) { }
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            SwitcherCore core = new SwitcherCore();

            // Any unexpected crash is written to logs\Error_*.txt instead of disappearing
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
            Application.ThreadException += (s, e) =>
            {
                core.RecordException("ERR_UNHANDLED", e.Exception);
                MessageBox.Show("Đã xảy ra lỗi không mong muốn:\r\n" + e.Exception.Message +
                    "\r\n\r\nChi tiết đã được ghi vào thư mục:\r\n" + core.ErrorLogDir,
                    "Antigravity Account Switcher", MessageBoxButtons.OK, MessageBoxIcon.Error);
            };
            AppDomain.CurrentDomain.UnhandledException += (s, e) =>
            {
                Exception ex = e.ExceptionObject as Exception;
                if (ex != null) core.RecordException("ERR_FATAL", ex);
            };

            if (args != null && args.Length > 0 && (args[0] == "--init-slot1" || args[0] == "--save-current"))
            {
                string msg;
                int savedIdx;
                bool ok = core.SaveOrUpdateCurrentAccount(null, out msg, out savedIdx);
                return ok ? 0 : 1;
            }

            bool createdNew;
            using (Mutex mutex = new Mutex(true, "Global\\AntigravityAccountSwitcher_SingleInstance", out createdNew))
            {
                if (!createdNew)
                {
                    MessageBox.Show("Antigravity Account Switcher đang chạy rồi.\r\nBấm OK để mở lại cửa sổ đang chạy.",
                        "Antigravity Account Switcher", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    // Let the running instance take the foreground, then ask it to show itself
                    AllowSetForegroundWindow(ASFW_ANY);
                    if (WM_SHOW_RUNNING_INSTANCE != 0) PostMessage(HWND_BROADCAST, WM_SHOW_RUNNING_INSTANCE, IntPtr.Zero, IntPtr.Zero);
                    return 0;
                }

                Application.Run(new MainForm(core));
            }
            return 0;
        }
    }
}
