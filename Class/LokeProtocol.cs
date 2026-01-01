using System;
using System.Text;
using System.IO.Ports;
using System.Threading;

namespace Odin_Flash.Class
{
    /// <summary>
    /// Protocolo LOKE basado en el análisis de Ghidra
    /// Constantes y métodos estáticos extraídos de FUN_00434d50 y FUN_00435ad0
    /// </summary>
    public static class LokeProtocol
    {
        // Constantes extraídas de Ghidra
        /// <summary>
        /// Magic string enviado en FUN_00434d50 para iniciar el handshake
        /// </summary>
        public const string MAGIC_ODIN = "ODIN"; // Enviado en FUN_00434d50
        
        /// <summary>
        /// Magic string respuesta esperada de DAT_0062d12c
        /// </summary>
        public const string MAGIC_LOKE = "LOKE"; // Respuesta esperada de DAT_0062d12c
        
        /// <summary>
        /// Tamaño de segmento para comandos de control y PIT (FUN_00435ad0)
        /// </summary>
        public const int CHUNK_CONTROL = 500;    // Segmentación vista en FUN_00435ad0
        
        /// <summary>
        /// Tamaño de bloque estándar (0x20000) para archivos .img
        /// </summary>
        public const int CHUNK_DATA = 131072;    // Bloque estándar (0x20000) para .img
        
        /// <summary>
        /// Delay de estabilidad visto en 0042e470 (máquina de estados)
        /// </summary>
        public const int DELAY_STABILITY = 100;  // Sleep(100) visto en 0042e470

        /// <summary>
        /// Ejecuta el saludo inicial del protocolo Loke (Ref: FUN_00434d50)
        /// Envía "ODIN" y espera "LOKE" como respuesta
        /// </summary>
        /// <param name="port">Puerto serial abierto y configurado</param>
        /// <returns>True si el handshake fue exitoso (recibió "LOKE")</returns>
        public static bool PerformHandshake(SerialPort port)
        {
            try
            {
                if (port == null || !port.IsOpen)
                    return false;

                byte[] hello = Encoding.ASCII.GetBytes(MAGIC_ODIN);
                port.Write(hello, 0, hello.Length);
                
                Thread.Sleep(DELAY_STABILITY);

                byte[] response = new byte[4];
                int read = port.Read(response, 0, 4);
                string responseStr = Encoding.ASCII.GetString(response, 0, read);

                return responseStr == MAGIC_LOKE;
            }
            catch 
            { 
                return false; 
            }
        }

        /// <summary>
        /// Envía datos en segmentos de 500 bytes (para PIT y Comandos)
        /// Ref: Bucle en FUN_00435ad0
        /// </summary>
        /// <param name="port">Puerto serial abierto y configurado</param>
        /// <param name="data">Datos a enviar</param>
        /// <returns>True si la transferencia fue exitosa</returns>
        public static bool SendSegmented(SerialPort port, byte[] data)
        {
            if (port == null || !port.IsOpen || data == null || data.Length == 0)
                return false;

            int total = data.Length;
            int chunks = (total + CHUNK_CONTROL - 1) / CHUNK_CONTROL;

            for (int i = 0; i < chunks; i++)
            {
                int offset = i * CHUNK_CONTROL;
                int count = Math.Min(CHUNK_CONTROL, total - offset);

                try
                {
                    port.Write(data, offset, count);
                    
                    // Odin espera un ACK (0x06) después de procesar paquetes de control
                    // Si el puerto tiene datos, leemos el byte de estado
                    Thread.Sleep(10); // Delay pequeño entre bloques
                    
                    if (port.BytesToRead > 0)
                    {
                        int ack = port.ReadByte();
                        if (ack != 0x06) 
                            return false; // ACK esperado no recibido
                    }
                }
                catch 
                { 
                    return false; 
                }
            }
            return true;
        }

        /// <summary>
        /// Envía datos en segmentos grandes de 128KB (0x20000) para archivos de imagen
        /// Optimizado para transferencias de firmware grandes (system.img, boot.img, super.img, etc.)
        /// IMPORTANTE: Usa máximo 131,072 bytes (CHUNK_DATA) por bloque para evitar saturación de Windows
        /// Si se usa un buffer mayor a 1MB en C#, Windows se satura (Ref: análisis de super.img 6.5GB)
        /// </summary>
        /// <param name="port">Puerto serial abierto y configurado</param>
        /// <param name="data">Datos a enviar</param>
        /// <returns>True si la transferencia fue exitosa</returns>
        public static bool SendLargeSegmented(SerialPort port, byte[] data)
        {
            if (port == null || !port.IsOpen || data == null || data.Length == 0)
                return false;

            int total = data.Length;
            int chunks = (total + CHUNK_DATA - 1) / CHUNK_DATA;

            // Asegurar que nunca excedamos 1MB por operación (Windows se satura)
            // CHUNK_DATA (131,072 bytes) está bien por debajo del límite
            const int MAX_SAFE_CHUNK = 1024 * 1024; // 1MB máximo
            if (CHUNK_DATA > MAX_SAFE_CHUNK)
            {
                throw new InvalidOperationException($"CHUNK_DATA ({CHUNK_DATA}) excede el límite seguro de {MAX_SAFE_CHUNK} bytes");
            }

            for (int i = 0; i < chunks; i++)
            {
                int offset = i * CHUNK_DATA;
                int count = Math.Min(CHUNK_DATA, total - offset);

                try
                {
                    // Escribir exactamente CHUNK_DATA bytes (131,072) - nunca más
                    port.Write(data, offset, count);
                    
                    // Delay pequeño entre bloques grandes
                    Thread.Sleep(10);
                    
                    // Verificar ACK si está disponible (no bloqueante)
                    if (port.BytesToRead > 0)
                    {
                        int ack = port.ReadByte();
                        if (ack != 0x06) 
                            return false;
                    }
                }
                catch 
                { 
                    return false; 
                }
            }
            return true;
        }
    }
}

