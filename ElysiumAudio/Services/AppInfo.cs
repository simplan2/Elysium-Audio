using System;
using System.Reflection;

namespace ElysiumAudio.Services
{
    /// <summary>
    /// Datos del producto leídos de los metadatos del ensamblado, definidos en el
    /// .csproj (Version, Company, Authors, Copyright). Los muestra el modal de
    /// Ajustes, para no tenerlos escritos a mano en dos sitios.
    /// </summary>
    public static class AppInfo
    {
        private static readonly Assembly Self = typeof(AppInfo).Assembly;

        public static string Version { get; } = ReadVersion();
        public static string Company { get; } = Read<AssemblyCompanyAttribute>()?.Company ?? "";
        public static string Copyright { get; } = Read<AssemblyCopyrightAttribute>()?.Copyright ?? "";

        /// <summary>Autor, tomado del AssemblyMetadata "Author" del .csproj.</summary>
        public static string Author { get; } = ReadAuthor();

        private static string ReadVersion()
        {
            var info = Read<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
            if (string.IsNullOrWhiteSpace(info))
            {
                return Self.GetName().Version?.ToString(3) ?? "1.0.0";
            }

            // "1.0.0+abc1234" -> "1.0.0"
            int plus = info.IndexOf('+');
            return plus > 0 ? info[..plus] : info;
        }

        private static string ReadAuthor()
        {
            foreach (var meta in Self.GetCustomAttributes<AssemblyMetadataAttribute>())
            {
                if (string.Equals(meta.Key, "Author", StringComparison.OrdinalIgnoreCase))
                {
                    return meta.Value ?? "";
                }
            }

            return "";
        }

        private static T? Read<T>() where T : Attribute => Self.GetCustomAttribute<T>();
    }
}
