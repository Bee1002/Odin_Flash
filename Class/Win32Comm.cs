using System;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using System.IO.Ports;
using System.Reflection;

namespace Odin_Flash.Class
{
    /// <summary>
    /// Utilidad nativa para manejo de comunicación serial a nivel de kernel
    /// Basado en el análisis de Ghidra (offset 00438342) donde Odin gestiona errores directamente con Windows
    /// </summary>
    public static class Win32Comm
    {
        // Importación de ClearCommError (lo que vimos en el offset 00438342)
        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool ClearCommError(SafeFileHandle hFile, out uint lpErrors, IntPtr lpStat);

        // Importación de PurgeComm para limpiar el canal después de archivos grandes
        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool PurgeComm(SafeFileHandle hFile, uint dwFlags);

        // Constantes para PurgeComm
        public const uint PURGE_TXABORT = 0x0001;  // Termina todas las operaciones de escritura pendientes
        public const uint PURGE_RXABORT = 0x0002;  // Termina todas las operaciones de lectura pendientes
        public const uint PURGE_TXCLEAR = 0x0004;  // Limpia el buffer de transmisión
        public const uint PURGE_RXCLEAR = 0x0008;  // Limpia el buffer de recepción

        /// <summary>
        /// Resetea el puerto serie usando funciones nativas de Windows
        /// Imita el código que vimos en la dirección 00438342 de Odin
        /// </summary>
        /// <param name="port">Puerto serial a resetear</param>
        /// <returns>True si el reset fue exitoso</returns>
        public static bool ResetPort(SerialPort port)
        {
            if (port == null || !port.IsOpen)
                return false;

            try
            {
                // Obtenemos el Handle del puerto serie usando reflexión
                // SerialPort.BaseStream es un FileStream que tiene un _handle privado
                object baseStream = port.BaseStream;
                if (baseStream == null)
                    return false;

                // Obtener el campo _handle usando reflexión
                FieldInfo handleField = baseStream.GetType().GetField("_handle", 
                    BindingFlags.NonPublic | BindingFlags.Instance);
                
                if (handleField == null)
                {
                    // Intentar con otro nombre común
                    handleField = baseStream.GetType().GetField("_fileHandle", 
                        BindingFlags.NonPublic | BindingFlags.Instance);
                }

                if (handleField == null)
                {
                    // Si no podemos acceder al handle, usar métodos públicos
                    port.DiscardInBuffer();
                    port.DiscardOutBuffer();
                    return true;
                }

                SafeFileHandle handle = handleField.GetValue(baseStream) as SafeFileHandle;
                if (handle == null || handle.IsInvalid)
                {
                    // Fallback a métodos públicos
                    port.DiscardInBuffer();
                    port.DiscardOutBuffer();
                    return true;
                }

                uint errors;
                // 1. Limpiamos errores de hardware (Como Odin original en 00438342)
                bool clearResult = ClearCommError(handle, out errors, IntPtr.Zero);
                
                // 2. Purgamos cualquier dato residual que causó el error de E/S
                // Esto limpia completamente los buffers y cancela operaciones pendientes
                uint purgeFlags = PURGE_TXABORT | PURGE_RXABORT | PURGE_TXCLEAR | PURGE_RXCLEAR;
                bool purgeResult = PurgeComm(handle, purgeFlags);

                return clearResult && purgeResult;
            }
            catch (Exception)
            {
                // Si falla el acceso nativo, usar métodos públicos como fallback
                try
                {
                    if (port.IsOpen)
                    {
                        port.DiscardInBuffer();
                        port.DiscardOutBuffer();
                        return true;
                    }
                }
                catch
                {
                    // Si todo falla, retornar false
                }
                return false;
            }
        }

        /// <summary>
        /// Limpia solo los errores de comunicación sin purgar buffers
        /// Útil cuando solo necesitas verificar el estado del puerto
        /// </summary>
        /// <param name="port">Puerto serial a verificar</param>
        /// <param name="errors">Código de error devuelto por Windows</param>
        /// <returns>True si la operación fue exitosa</returns>
        public static bool GetCommErrors(SerialPort port, out uint errors)
        {
            errors = 0;
            if (port == null || !port.IsOpen)
                return false;

            try
            {
                object baseStream = port.BaseStream;
                if (baseStream == null)
                    return false;

                FieldInfo handleField = baseStream.GetType().GetField("_handle", 
                    BindingFlags.NonPublic | BindingFlags.Instance);
                
                if (handleField == null)
                    return false;

                SafeFileHandle handle = handleField.GetValue(baseStream) as SafeFileHandle;
                if (handle == null || handle.IsInvalid)
                    return false;

                return ClearCommError(handle, out errors, IntPtr.Zero);
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Purgar buffers del puerto usando funciones nativas
        /// Más agresivo que DiscardInBuffer/DiscardOutBuffer
        /// </summary>
        /// <param name="port">Puerto serial a purgar</param>
        /// <param name="clearTx">Limpiar buffer de transmisión</param>
        /// <param name="clearRx">Limpiar buffer de recepción</param>
        /// <returns>True si la operación fue exitosa</returns>
        public static bool PurgePort(SerialPort port, bool clearTx = true, bool clearRx = true)
        {
            if (port == null || !port.IsOpen)
                return false;

            try
            {
                object baseStream = port.BaseStream;
                if (baseStream == null)
                    return false;

                FieldInfo handleField = baseStream.GetType().GetField("_handle", 
                    BindingFlags.NonPublic | BindingFlags.Instance);
                
                if (handleField == null)
                {
                    // Fallback
                    if (clearTx) port.DiscardOutBuffer();
                    if (clearRx) port.DiscardInBuffer();
                    return true;
                }

                SafeFileHandle handle = handleField.GetValue(baseStream) as SafeFileHandle;
                if (handle == null || handle.IsInvalid)
                {
                    // Fallback
                    if (clearTx) port.DiscardOutBuffer();
                    if (clearRx) port.DiscardInBuffer();
                    return true;
                }

                uint purgeFlags = 0;
                if (clearTx) purgeFlags |= PURGE_TXABORT | PURGE_TXCLEAR;
                if (clearRx) purgeFlags |= PURGE_RXABORT | PURGE_RXCLEAR;

                return PurgeComm(handle, purgeFlags);
            }
            catch
            {
                // Fallback a métodos públicos
                try
                {
                    if (port.IsOpen)
                    {
                        if (clearTx) port.DiscardOutBuffer();
                        if (clearRx) port.DiscardInBuffer();
                        return true;
                    }
                }
                catch { }
                return false;
            }
        }
    }
}


