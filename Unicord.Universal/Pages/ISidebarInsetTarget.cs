namespace Unicord.Universal.Pages
{
    /// <summary>
    /// Implemented by the pages hosted in the sidebar frame, so the floating user panel can reserve
    /// room for itself without resizing them.
    ///
    /// The reservation cannot be padding on the frame's container. That is the scrolling list's
    /// viewport, and changing a viewport mid-scroll makes a virtualised list re-realise from the
    /// top - joining a call, which grows the panel, threw the channel list back to the first
    /// channel. Padding the list's own content instead lets the last row clear the panel while the
    /// viewport, and the offset into it, stay exactly where they were.
    /// </summary>
    internal interface ISidebarInsetTarget
    {
        /// <summary>
        /// Space to leave clear at the bottom of the scrollable content, in effective pixels.
        /// </summary>
        void SetBottomInset(double inset);
    }
}
