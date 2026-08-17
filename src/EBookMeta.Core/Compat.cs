// Everything .NET Framework 4.8 is missing, in one file. Mandatory, not decorative:
// delete the attribute polyfills below and the project stops compiling, because
// `record`, `init` and `required` look their support types up by name. A retarget to
// modern .NET deletes this whole file with no call site changing.

using System.Text;

namespace EBookMeta.Compat
{
    /// <summary>
    /// Guard-clause helpers standing in for the static throw methods added to
    /// <see cref="ArgumentNullException"/> and friends after .NET Framework 4.8.
    /// </summary>
    internal static class Throw
    {
        /// <summary>Throws if <paramref name="value"/> is null.</summary>
        /// <param name="value">The argument to check.</param>
        /// <param name="name">The parameter name, supplied automatically.</param>
        /// <exception cref="ArgumentNullException"><paramref name="value"/> is null.</exception>
        internal static void IfNull(
            object? value,
            [System.Runtime.CompilerServices.CallerArgumentExpression(nameof(value))] string? name = null)
        {
            if (value is null)
            {
                throw new ArgumentNullException(name);
            }
        }

        /// <summary>Throws if <paramref name="value"/> is null or empty.</summary>
        /// <param name="value">The argument to check.</param>
        /// <param name="name">The parameter name, supplied automatically.</param>
        /// <exception cref="ArgumentNullException"><paramref name="value"/> is null.</exception>
        /// <exception cref="ArgumentException"><paramref name="value"/> is empty.</exception>
        internal static void IfNullOrEmpty(
            string? value,
            [System.Runtime.CompilerServices.CallerArgumentExpression(nameof(value))] string? name = null)
        {
            if (value is null)
            {
                throw new ArgumentNullException(name);
            }

            if (value.Length == 0)
            {
                throw new ArgumentException("Value cannot be empty.", name);
            }
        }

        /// <summary>Throws if the owning object has been disposed.</summary>
        /// <param name="disposed">Whether the object is disposed.</param>
        /// <param name="instance">The object, for the exception's type name.</param>
        /// <exception cref="ObjectDisposedException">It has been disposed.</exception>
        internal static void IfDisposed(bool disposed, object instance)
        {
            if (disposed)
            {
                throw new ObjectDisposedException(instance.GetType().FullName);
            }
        }
    }

    /// <summary>Stand-ins for BCL methods added after .NET Framework 4.8.</summary>
    internal static class BclShims
    {
        /// <summary>Reads exactly <paramref name="buffer"/>.Length bytes, or throws.</summary>
        /// <param name="stream">The stream to read.</param>
        /// <param name="buffer">The buffer to fill.</param>
        /// <exception cref="EndOfStreamException">The stream ended early.</exception>
        internal static void ReadExactly(this Stream stream, byte[] buffer)
        {
            int total = 0;
            while (total < buffer.Length)
            {
                int read = stream.Read(buffer, total, buffer.Length - total);
                if (read == 0)
                {
                    throw new EndOfStreamException(
                        $"Expected {buffer.Length} bytes but the stream ended after {total}.");
                }

                total += read;
            }
        }

        /// <summary>Reads up to <paramref name="count"/> bytes, tolerating an early end.</summary>
        /// <param name="stream">The stream to read.</param>
        /// <param name="buffer">The buffer to fill.</param>
        /// <param name="count">The number of bytes wanted.</param>
        /// <param name="throwOnEndOfStream">Whether a short read is an error.</param>
        /// <returns>The number of bytes actually read.</returns>
        internal static int ReadAtLeast(this Stream stream, byte[] buffer, int count, bool throwOnEndOfStream)
        {
            int total = 0;
            while (total < count)
            {
                int read = stream.Read(buffer, total, count - total);
                if (read == 0)
                {
                    if (throwOnEndOfStream)
                    {
                        throw new EndOfStreamException(
                            $"Expected {count} bytes but the stream ended after {total}.");
                    }

                    break;
                }

                total += read;
            }

            return total;
        }

        /// <summary>Decodes a span of bytes.</summary>
        /// <param name="encoding">The encoding to decode with.</param>
        /// <param name="bytes">The bytes to decode.</param>
        /// <returns>The decoded string.</returns>
        internal static string GetString(this Encoding encoding, ReadOnlySpan<byte> bytes) =>
            bytes.IsEmpty ? string.Empty : encoding.GetString(bytes.ToArray());

        /// <summary>Whether the string starts with a character.</summary>
        /// <param name="value">The string to test.</param>
        /// <param name="c">The character to look for.</param>
        /// <returns><see langword="true"/> if it does.</returns>
        internal static bool StartsWith(this string value, char c) =>
            value.Length > 0 && value[0] == c;

        /// <summary>Whether the string ends with a character.</summary>
        /// <param name="value">The string to test.</param>
        /// <param name="c">The character to look for.</param>
        /// <returns><see langword="true"/> if it does.</returns>
        internal static bool EndsWith(this string value, char c) =>
            value.Length > 0 && value[value.Length - 1] == c;

        /// <summary>Whether the string contains a substring, using the given comparison.</summary>
        /// <param name="value">The string to search.</param>
        /// <param name="needle">The substring to find.</param>
        /// <param name="comparison">How to compare.</param>
        /// <returns><see langword="true"/> if found.</returns>
        internal static bool Contains(this string value, string needle, StringComparison comparison) =>
            value.IndexOf(needle, comparison) >= 0;

        /// <summary>Splits on a single character with the given options.</summary>
        /// <param name="value">The string to split.</param>
        /// <param name="separator">The separator character.</param>
        /// <param name="options">Split options.</param>
        /// <returns>The parts.</returns>
        internal static string[] Split(this string value, char separator, StringSplitOptions options) =>
            value.Split(new[] { separator }, options);
    }

    /// <summary>Encodings that modern .NET exposes as static properties.</summary>
    internal static class Encodings
    {
        /// <summary>ISO-8859-1. Maps every byte to a character, so it never throws.</summary>
        internal static Encoding Latin1 { get; } = Encoding.GetEncoding(28591);
    }
}

// Compiler-recognised types net48 does not ship. Looked up by full name only, so
// declaring them here enables `init`, `record` and `required` with no dependency.

#pragma warning disable CS9113 // Parameter is unread — these are marker attributes.

namespace System.Runtime.CompilerServices
{
    /// <summary>
    /// Marker the compiler requires to emit <c>init</c> accessors, and therefore
    /// <c>record</c> types with init-only properties.
    /// </summary>
    [System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverage]
    internal static class IsExternalInit
    {
    }

    /// <summary>
    /// Lets a parameter capture the source text of another argument, so a guard
    /// clause can name the expression that failed without the caller repeating it
    /// in a <c>nameof</c>.
    /// </summary>
    [AttributeUsage(AttributeTargets.Parameter, AllowMultiple = false, Inherited = false)]
    [System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverage]
    internal sealed class CallerArgumentExpressionAttribute(string parameterName) : Attribute
    {
        /// <summary>The parameter whose source text is captured.</summary>
        public string ParameterName { get; } = parameterName;
    }

    /// <summary>Marks a member as required to be initialised by an object initialiser.</summary>
    [AttributeUsage(
        AttributeTargets.Class | AttributeTargets.Struct | AttributeTargets.Field | AttributeTargets.Property,
        AllowMultiple = false,
        Inherited = false)]
    [System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverage]
    internal sealed class RequiredMemberAttribute : Attribute
    {
    }

    /// <summary>
    /// Indicates that compiler support for a feature is required to consume the
    /// annotated member. Emitted alongside <see cref="RequiredMemberAttribute"/>
    /// so an older compiler refuses the type rather than silently skipping the
    /// required-member check.
    /// </summary>
    [AttributeUsage(AttributeTargets.All, AllowMultiple = true, Inherited = false)]
    [System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverage]
    internal sealed class CompilerFeatureRequiredAttribute(string featureName) : Attribute
    {
        /// <summary>The name of the required compiler feature.</summary>
        public string FeatureName { get; } = featureName;

        /// <summary>Whether a compiler that does not recognise the feature may ignore it.</summary>
        public bool IsOptional { get; init; }

        /// <summary>The <c>RefStructs</c> feature name.</summary>
        public const string RefStructs = nameof(RefStructs);

        /// <summary>The <c>RequiredMembers</c> feature name.</summary>
        public const string RequiredMembers = nameof(RequiredMembers);
    }
}

namespace System.Diagnostics.CodeAnalysis
{
    /// <summary>
    /// Tells the compiler that a constructor initialises every required member,
    /// so callers need not set them in an object initialiser.
    /// </summary>
    [AttributeUsage(AttributeTargets.Constructor, AllowMultiple = false, Inherited = false)]
    [ExcludeFromCodeCoverage]
    internal sealed class SetsRequiredMembersAttribute : Attribute
    {
    }
}

#pragma warning restore CS9113
