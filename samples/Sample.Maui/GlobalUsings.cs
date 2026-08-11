global using System.Collections.ObjectModel;
global using CommunityToolkit.Mvvm.ComponentModel;
global using CommunityToolkit.Mvvm.Input;
global using Shiny;

// IMainThread lives here rather than in Shiny: it is the marshalling abstraction the navigator and
// dialogs use internally, and it works around the platforms where MAUI's own MainThread helper does
// not behave (macOS, Linux).
global using Shiny.Infrastructure;
