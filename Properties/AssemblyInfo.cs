using System.Runtime.CompilerServices;

// Expose Clay's internal text-measurement machinery to the text-input plugin,
// mirroring how clay_text_input.h reaches Clay__MeasureText / the error handler.
[assembly: InternalsVisibleTo("ClaySharp.Plugin.TextInput")]
