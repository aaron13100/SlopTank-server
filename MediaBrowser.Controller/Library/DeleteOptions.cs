// SlopTank modification notice: added or changed by SlopTank on 2026-11-13.
#pragma warning disable CS1591

namespace MediaBrowser.Controller.Library
{
    public class DeleteOptions
    {
        public DeleteOptions()
        {
            DeleteFromExternalProvider = true;
        }

        public bool DeleteFileLocation { get; set; }

        public bool DeleteFromExternalProvider { get; set; }

        /// <summary>Gets or sets the known origin of the library removal.</summary>
        public LibraryRemovalReason RemovalReason { get; set; } = LibraryRemovalReason.Library;
    }
}
