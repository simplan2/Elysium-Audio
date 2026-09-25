namespace ElysiumAudio.Models
{
    // Propósito activo de la aplicación. Determina qué función ejecuta el botón principal:
    // normalizar loudness/true peak, o solo medir (analizar) sin modificar los archivos.
    public enum AppMode
    {
        Normalize,
        Analyze
    }
}
