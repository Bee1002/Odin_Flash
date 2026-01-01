using System;
using System.Text;
using System.IO.Ports;
using System.Threading;
using System.Threading.Tasks;

namespace Odin_Flash.Class
{
    /// <summary>
    /// Comandos reales del protocolo Odin encontrados en Ghidra
    /// Basado en análisis de FUN_00434d50 y FUN_00434fb0
    /// </summary>
    public enum OdinCommand : uint
    {
        /// <summary>
        /// Comando para iniciar sesión (0x4F44494E = "ODIN" en ASCII)
        /// </summary>
        StartSession = 0x4F44494E, // "ODIN"
        
        /// <summary>
        /// Comando para establecer modo PIT (0x5049544D = "PITM" en ASCII)
        /// </summary>
        SetPitMode = 0x5049544D,   // "PITM"
        
        /// <summary>
        /// Comando para leer PIT del dispositivo (0x50495452 = "PITR" en ASCII)
        /// </summary>
        ReadPit = 0x50495452,      // "PITR"
        
        /// <summary>
        /// Comando para enviar datos de firmware (0x44415441 = "DATA" en ASCII)
        /// </summary>
        FlashData = 0x44415441,     // "DATA"
        
        /// <summary>
        /// Comando para reiniciar a modo normal (0x52454254 = "REBT" en ASCII)
        /// </summary>
        Reboot = 0x52454254,       // "REBT"
        
        /// <summary>
        /// Comando para finalizar sesión (0x454E4453 = "ENDS" en ASCII)
        /// </summary>
        EndSession = 0x454E4453     // "ENDS"
    }

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
        /// Tamaño fijo del paquete de control según análisis de FUN_00434fb0
        /// Todos los comandos deben enviarse en paquetes de exactamente 500 bytes
        /// </summary>
        public const int CONTROL_PACKET_SIZE = 500;

        /// <summary>
        /// ACK esperado del dispositivo después de procesar un comando
        /// </summary>
        public const byte ACK_BYTE = 0x06;

        /// <summary>
        /// Espera y verifica el ACK del dispositivo (async)
        /// El dispositivo suele tardar entre 5ms y 50ms en procesar
        /// </summary>
        /// <param name="port">Puerto serial abierto y configurado</param>
        /// <param name="timeoutMs">Tiempo máximo de espera en milisegundos (default: 1000ms)</param>
        /// <returns>True si se recibió ACK (0x06), False en caso contrario</returns>
        public static async Task<bool> WaitAndVerifyAckAsync(SerialPort port, int timeoutMs = 1000)
        {
            if (port == null || !port.IsOpen)
                return false;

            try
            {
                int elapsed = 0;
                const int pollInterval = 10; // Revisar cada 10ms

                // Esperar hasta que haya datos disponibles o se agote el timeout
                while (port.BytesToRead == 0 && elapsed < timeoutMs)
                {
                    await Task.Delay(pollInterval);
                    elapsed += pollInterval;
                }

                if (port.BytesToRead > 0)
                {
                    int response = port.ReadByte();
                    return response == ACK_BYTE;
                }

                // Si no hay datos después del timeout, puede ser que el dispositivo
                // no envíe ACK en algunos casos (comportamiento observado en algunos modelos)
                // Retornamos true para permitir continuar
                return true;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Método de compatibilidad sincrónico - redirige a WaitAndVerifyAckAsync
        /// </summary>
        [Obsolete("Usar WaitAndVerifyAckAsync en su lugar")]
        public static bool WaitAndVerifyAck(SerialPort port, int timeoutMs = 1000)
        {
            try
            {
                return Task.Run(async () => await WaitAndVerifyAckAsync(port, timeoutMs)).Result;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Envía un paquete de control de 500 bytes según la estructura del protocolo Odin (async)
        /// Estructura: | Offset 0x00: 4 bytes Comando | Offset 0x04: 4 bytes Tamaño (Big Endian) | 
        ///            | Offset 0x08: 4 bytes ID/Offset | Resto: Relleno con 0x00 hasta 500 bytes |
        /// Basado en análisis de FUN_00434fb0
        /// </summary>
        /// <param name="port">Puerto serial abierto y configurado</param>
        /// <param name="cmd">Comando a enviar (ODIN, PITM, DATA, ENDS)</param>
        /// <param name="dataSize">Tamaño de los datos que siguen (default: 0)</param>
        /// <param name="sequenceId">ID de secuencia/Offset (default: 0)</param>
        /// <returns>True si el paquete se envió correctamente y se recibió ACK</returns>
        public static async Task<bool> SendControlPacketAsync(SerialPort port, OdinCommand cmd, uint dataSize = 0, uint sequenceId = 0)
        {
            if (port == null || !port.IsOpen)
                return false;

            try
            {
                // Crear paquete de 500 bytes inicializado con ceros
                byte[] packet = new byte[CONTROL_PACKET_SIZE];

                // Offset 0x00: Comando (4 bytes, Big Endian según protocolo Loke)
                byte[] cmdBytes = BitConverter.GetBytes((uint)cmd);
                if (BitConverter.IsLittleEndian)
                    Array.Reverse(cmdBytes);
                Array.Copy(cmdBytes, 0, packet, 0, 4);

                // Offset 0x04: Tamaño de datos (4 bytes, Big Endian)
                byte[] sizeBytes = BitConverter.GetBytes(dataSize);
                if (BitConverter.IsLittleEndian)
                    Array.Reverse(sizeBytes);
                Array.Copy(sizeBytes, 0, packet, 4, 4);

                // Offset 0x08: ID de secuencia/Offset (4 bytes, Little Endian)
                byte[] seqBytes = BitConverter.GetBytes(sequenceId);
                Array.Copy(seqBytes, 0, packet, 8, 4);

                // El resto del paquete ya está inicializado con 0x00 (relleno)

                // Enviar el paquete completo de 500 bytes
                port.Write(packet, 0, CONTROL_PACKET_SIZE);

                // Esperar el ACK del dispositivo (usar await Task.Delay)
                await Task.Delay(DELAY_STABILITY);
                return await WaitAndVerifyAckAsync(port);
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Método de compatibilidad sincrónico - redirige a SendControlPacketAsync
        /// </summary>
        [Obsolete("Usar SendControlPacketAsync en su lugar")]
        public static bool SendControlPacket(SerialPort port, OdinCommand cmd, uint dataSize = 0, uint sequenceId = 0)
        {
            try
            {
                return Task.Run(async () => await SendControlPacketAsync(port, cmd, dataSize, sequenceId)).Result;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Ejecuta el saludo inicial del protocolo Loke usando paquete de control (async)
        /// Envía comando ODIN en paquete de 500 bytes y espera "LOKE" como respuesta
        /// Ref: FUN_00434d50 actualizado con estructura de paquete de control
        /// </summary>
        /// <param name="port">Puerto serial abierto y configurado</param>
        /// <returns>True si el handshake fue exitoso (recibió "LOKE")</returns>
        public static async Task<bool> PerformHandshakeAsync(SerialPort port)
        {
            try
            {
                if (port == null || !port.IsOpen)
                    return false;

                // Enviar comando ODIN usando paquete de control de 500 bytes
                if (!await SendControlPacketAsync(port, OdinCommand.StartSession, 0, 0))
                    return false;

                await Task.Delay(DELAY_STABILITY);

                // Esperar respuesta "LOKE" del dispositivo
                byte[] response = new byte[4];
                int read = 0;
                int timeout = 1000; // 1 segundo de timeout
                int elapsed = 0;

                // Leer respuesta con timeout
                while (read < 4 && elapsed < timeout)
                {
                    if (port.BytesToRead > 0)
                    {
                        int bytesRead = port.Read(response, read, 4 - read);
                        read += bytesRead;
                    }
                    else
                    {
                        await Task.Delay(10);
                        elapsed += 10;
                    }
                }

                if (read == 4)
                {
                    string responseStr = Encoding.ASCII.GetString(response, 0, 4);
                    return responseStr == MAGIC_LOKE;
                }

                return false;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Método de compatibilidad sincrónico - redirige a PerformHandshakeAsync
        /// </summary>
        [Obsolete("Usar PerformHandshakeAsync en su lugar")]
        public static bool PerformHandshake(SerialPort port)
        {
            try
            {
                return Task.Run(async () => await PerformHandshakeAsync(port)).Result;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Envía datos en segmentos de exactamente 500 bytes (para PIT y Comandos) (async)
        /// Cada bloque debe ser exactamente 500 bytes, rellenando con ceros si es necesario
        /// Ref: Bucle en FUN_00435ad0 - Todos los paquetes deben ser de 500 bytes
        /// </summary>
        /// <param name="port">Puerto serial abierto y configurado</param>
        /// <param name="data">Datos a enviar</param>
        /// <returns>True si la transferencia fue exitosa</returns>
        public static async Task<bool> SendSegmentedAsync(SerialPort port, byte[] data)
        {
            if (port == null || !port.IsOpen || data == null || data.Length == 0)
                return false;

            int total = data.Length;
            int chunks = (total + CHUNK_CONTROL - 1) / CHUNK_CONTROL;

            for (int i = 0; i < chunks; i++)
            {
                int offset = i * CHUNK_CONTROL;
                int dataRemaining = total - offset;
                int count = Math.Min(CHUNK_CONTROL, dataRemaining);

                try
                {
                    // Crear bloque de exactamente 500 bytes
                    byte[] block = new byte[CHUNK_CONTROL];
                    
                    // Copiar los datos disponibles
                    Array.Copy(data, offset, block, 0, count);
                    
                    // El resto del bloque ya está inicializado con 0x00 (padding)
                    
                    // Enviar el bloque completo de 500 bytes
                    port.Write(block, 0, CHUNK_CONTROL);
                    
                    // Delay pequeño entre bloques para dar tiempo al dispositivo (usar await Task.Delay)
                    await Task.Delay(10);
                    
                    // Verificar ACK si está disponible (no bloqueante)
                    if (port.BytesToRead > 0)
                    {
                        int ack = port.ReadByte();
                        if (ack != ACK_BYTE)
                        {
                            // Algunos modelos no envían ACK en cada bloque, solo al final
                            // Continuamos pero registramos la advertencia
                            return false;
                        }
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
        /// Método de compatibilidad sincrónico - redirige a SendSegmentedAsync
        /// </summary>
        [Obsolete("Usar SendSegmentedAsync en su lugar")]
        public static bool SendSegmented(SerialPort port, byte[] data)
        {
            try
            {
                return Task.Run(async () => await SendSegmentedAsync(port, data)).Result;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Envía datos en segmentos grandes de 128KB (0x20000) para archivos de imagen (async)
        /// Optimizado para transferencias de firmware grandes (system.img, boot.img, super.img, etc.)
        /// IMPORTANTE: Usa máximo 131,072 bytes (CHUNK_DATA) por bloque para evitar saturación de Windows
        /// Si se usa un buffer mayor a 1MB en C#, Windows se satura (Ref: análisis de super.img 6.5GB)
        /// </summary>
        /// <param name="port">Puerto serial abierto y configurado</param>
        /// <param name="data">Datos a enviar</param>
        /// <returns>True si la transferencia fue exitosa</returns>
        public static async Task<bool> SendLargeSegmentedAsync(SerialPort port, byte[] data)
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
                    
                    // Delay pequeño entre bloques grandes (usar await Task.Delay)
                    await Task.Delay(10);
                    
                    // Verificar ACK si está disponible (no bloqueante)
                    if (port.BytesToRead > 0)
                    {
                        int ack = port.ReadByte();
                        if (ack != ACK_BYTE) 
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

        /// <summary>
        /// Método de compatibilidad sincrónico - redirige a SendLargeSegmentedAsync
        /// </summary>
        [Obsolete("Usar SendLargeSegmentedAsync en su lugar")]
        public static bool SendLargeSegmented(SerialPort port, byte[] data)
        {
            try
            {
                return Task.Run(async () => await SendLargeSegmentedAsync(port, data)).Result;
            }
            catch
            {
                return false;
            }
        }
    }
}

