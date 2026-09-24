using System;
using System.IO;
using Microsoft.VisualBasic.FileIO;

namespace KamCapture.Services
{
    /// <summary>
    /// Throwing a take away sends it to the Recycle Bin rather than deleting it.
    /// Retake and Discard never ask "are you sure" — that would slow down the
    /// thing they exist to make quick — so a mistaken click has to be
    /// recoverable instead.
    /// </summary>
    public static class RecycleBin
    {
        /// <summary>True if the file went to the Recycle Bin.</summary>
        public static bool Send(string? path)
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return false;
            try
            {
                FileSystem.DeleteFile(path, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin,
                                      UICancelOption.DoNothing);
                return !File.Exists(path);
            }
            catch (Exception ex)
            {
                Log.Warn($"Could not send '{path}' to the Recycle Bin: {ex.Message}");
                return false;
            }
        }
    }
}
