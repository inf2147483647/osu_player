using System.Runtime.InteropServices;

namespace OsuPlayer.Game.Net
{
    /// <summary>
    /// Minimal Win32 multi-select "open file" dialog (comdlg32). osu!framework exposes no
    /// system file picker in the referenced package, so the single API we need is bound directly.
    /// Must be called from a background thread: <see cref="PickMediaFiles"/> blocks until the user is done.
    /// </summary>
    public static class FilePicker
    {
        private const int ofn_pathmustexist = 0x00000800;
        private const int ofn_filemustexist = 0x00001000;
        private const int ofn_allowmultiselect = 0x00000200;
        private const int ofn_explorer = 0x00080000;

        /// <summary>Paths of the chosen files (empty when the user cancels).</summary>
        public static string[] PickMediaFiles(string title = "选择要导入的音乐 / osu! 谱面")
        {
            if (!OperatingSystem.IsWindows())
                return [];

            int bufferChars = 64 * 1024;
            IntPtr buffer = Marshal.AllocHGlobal(bufferChars * 2);

            try
            {
                // The dialog writes into this buffer, so start from an all-zero block.
                for (int i = 0; i < bufferChars * 2; i++)
                    Marshal.WriteByte(buffer, i, 0);

                var options = new OpenFileName
                {
                    lStructSize = Marshal.SizeOf<OpenFileName>(),
                    hwndOwner = findOwnWindow(),
                    lpstrFilter = buildFilter(),
                    nFilterIndex = 1,
                    lpstrFile = buffer,
                    nMaxFile = bufferChars,
                    lpstrTitle = title,
                    Flags = ofn_explorer | ofn_allowmultiselect | ofn_filemustexist | ofn_pathmustexist,
                };

                return GetOpenFileNameW(ref options) ? parse(buffer) : [];
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }

        private static string buildFilter()
            => "音乐与谱面\0*.mp3;*.flac;*.wav;*.ogg;*.m4a;*.aac;*.wma;*.opus;*.osz;*.olz\0"
               + "音频文件\0*.mp3;*.flac;*.wav;*.ogg;*.m4a;*.aac;*.wma;*.opus\0"
               + "osu! 谱面\0*.osz;*.olz\0"
               + "所有文件\0*.*\0\0";

        /// <summary>
        /// With OFN_EXPLORER a multiple selection comes back as "directory\0file1\0file2\0\0";
        /// a single selection is just "full path\0".
        /// </summary>
        private static string[] parse(IntPtr buffer)
        {
            var parts = new List<string>();
            int charOffset = 0;

            while (true)
            {
                string? part = Marshal.PtrToStringUni(buffer + charOffset * 2);

                if (string.IsNullOrEmpty(part))
                    break;

                parts.Add(part);
                charOffset += part.Length + 1;
            }

            if (parts.Count == 0)
                return [];

            if (parts.Count == 1)
                return [parts[0]];

            string directory = parts[0];
            return parts.Skip(1).Select(name => Path.Combine(directory, name)).ToArray();
        }

        /// <summary>Our own top-level window, so the dialog is modal to the player.</summary>
        private static IntPtr findOwnWindow()
        {
            uint processId = (uint)Environment.ProcessId;
            IntPtr found = IntPtr.Zero;

            EnumWindows((handle, _) =>
            {
                GetWindowThreadProcessId(handle, out uint windowProcessId);

                if (windowProcessId == processId && IsWindowVisible(handle))
                {
                    found = handle;
                    return false;
                }

                return true;
            }, IntPtr.Zero);

            return found;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct OpenFileName
        {
            public int lStructSize;
            public IntPtr hwndOwner;
            public IntPtr hInstance;
            public string? lpstrFilter;
            public IntPtr lpstrCustomFilter;
            public int nMaxCustFilter;
            public int nFilterIndex;
            public IntPtr lpstrFile;
            public int nMaxFile;
            public IntPtr lpstrFileTitle;
            public int nMaxFileTitle;
            public string? lpstrInitialDir;
            public string? lpstrTitle;
            public int Flags;
            public short nFileOffset;
            public short nFileExtension;
            public string? lpstrDefExt;
            public IntPtr lCustData;
            public IntPtr lpfnHook;
            public IntPtr lpTemplateName;
            public IntPtr pvReserved;
            public int dwReserved;
            public int FlagsEx;
        }

        private delegate bool EnumWindowsProc(IntPtr handle, IntPtr parameter);

        [DllImport("comdlg32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool GetOpenFileNameW(ref OpenFileName options);

        [DllImport("user32.dll")]
        private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr parameter);

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr handle, out uint processId);

        [DllImport("user32.dll")]
        private static extern bool IsWindowVisible(IntPtr handle);
    }
}
