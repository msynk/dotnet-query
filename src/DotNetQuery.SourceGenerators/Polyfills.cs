// Lets records compile for netstandard2.0, which predates init-only setters.
namespace System.Runtime.CompilerServices
{
    internal static class IsExternalInit
    {
    }
}
