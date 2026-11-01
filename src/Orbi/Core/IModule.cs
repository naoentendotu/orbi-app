using System.Windows;

namespace Orbi.Core;

/// <summary>One ring wedge. Each module is independent; add a new one by implementing this and registering it in App.</summary>
public interface IModule
{
    string Id { get; }
    string Title { get; }
    string Glyph { get; }      // Segoe Fluent Icons code point
    string Status { get; }     // short line shown in the ring center on hover
    FrameworkElement Preview { get; }   // shown on hover: the common actions
    FrameworkElement Expanded { get; }  // shown on click: everything
}
