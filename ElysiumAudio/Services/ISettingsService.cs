using ElysiumAudio.Models;

namespace ElysiumAudio.Services
{
    /// <summary>
    /// Abstracción del servicio que persiste la configuración del usuario.
    /// </summary>
    public interface ISettingsService
    {
        /// <summary>Carga la configuración del usuario (o valores por defecto si no existe).</summary>
        UserSettings Load();

        /// <summary>Guarda la configuración del usuario en el almacenamiento persistente.</summary>
        void Save(UserSettings settings);
    }
}
