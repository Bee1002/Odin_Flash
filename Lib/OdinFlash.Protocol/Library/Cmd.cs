using OdinFlash.Protocol.Port;
using OdinFlash.Protocol.structs;
using System;
using System.Threading.Tasks;

namespace OdinFlash.Protocol
{
    /// <summary>
    /// LOKE por COM. Cuidado: 0x64 == 100 decimal — el layout del paquete en <see cref="GetCmdBuff"/> define
    /// si la barra del teléfono recibe el total correcto (AP &gt;4 GiB necesita hi dword en +12).
    /// </summary>
    public class Cmd
    {
        public byte[] BufferRead = new byte[8];

        /// <summary>Variante LOKE detectada en la última inicialización (2–5).</summary>
        public long LokeVariant { get; private set; }

        private long GetVariant(byte[] responseBuff)
        {
            return (BitConverter.ToInt32(responseBuff, 4) & 0xFFFF0000L) >> 16;
        }

        private byte[] GetCmdBuff(SamsungLokeCommand loke)
        {
            byte[] array = new byte[1024];
            Array.Copy(BitConverter.GetBytes(loke.Cmd), 0, array, 0, 4);
            Array.Copy(BitConverter.GetBytes(loke.SeqCmd), 0, array, 4, 4);

            // CRÍTICO barra teléfono: 0x64 (100 dec) seq 2/5 = lo@+8, hi@+12. No usar solo BinaryType 8 bytes (rompe >4 GiB).
            if (loke.Cmd == 0x64 && (loke.SeqCmd == 2 || loke.SeqCmd == 5))
            {
                Array.Copy(BitConverter.GetBytes((int)loke.BinaryType), 0, array, 8, 4);
                Array.Copy(BitConverter.GetBytes(loke.SizeWritten), 0, array, 12, 4);
            }
            else if (loke.Cmd == 0x64)
            {
                Array.Copy(BitConverter.GetBytes(loke.BinaryType), 0, array, 8, 8);
            }
            else
            {
                Array.Copy(BitConverter.GetBytes((int)loke.BinaryType), 0, array, 8, 4);
                Array.Copy(BitConverter.GetBytes(loke.SizeWritten), 0, array, 12, 4);
            }

            Array.Copy(BitConverter.GetBytes(loke.Unknown), 0, array, 16, 4);
            Array.Copy(BitConverter.GetBytes(loke.DeviceId), 0, array, 20, 4);
            Array.Copy(BitConverter.GetBytes(loke.Identifier), 0, array, 24, 4);
            Array.Copy(BitConverter.GetBytes(loke.SessionEnd), 0, array, 28, 4);
            Array.Copy(BitConverter.GetBytes(loke.EfsClear), 0, array, 32, 4);
            Array.Copy(BitConverter.GetBytes(loke.BootUpdate), 0, array, 36, 4);
            return array;
        }

        public bool IsSuccessfulResponse(byte[] response)
        {
            return response != null && response.Length >= 8 && response[0] != byte.MaxValue;
        }

        public void ValidateResponse(byte[] response, string context)
        {
            if (response == null)
                throw new Exception($"{context}: no LOKE response.");
            if (response.Length < 8)
                throw new Exception($"{context}: incomplete LOKE response ({response.Length}/8 bytes).");
            if (response[0] == byte.MaxValue)
                throw new Exception($"{context}: invalid LOKE response 0xFF.");
        }

        public async Task<bool> LOKE_SendCMD(device device,SamsungLokeCommand Cmd, bool readresp = true)
        {
            byte[] cmdBuff = GetCmdBuff(Cmd);
            await device.WritePort(cmdBuff, cmdBuff.Length);
            Array.Clear(BufferRead, 0, 8);
            if (!readresp)
            {
                return true;
            }
            var num = await device.ReadExactAsync(BufferRead, 0, 8);
            if (num != 8)
                throw new Exception($"LOKE command 0x{Cmd.Cmd:X2}/0x{Cmd.SeqCmd:X2}: incomplete response ({num}/8 bytes).");

            ValidateResponse(BufferRead, $"LOKE command 0x{Cmd.Cmd:X2}/0x{Cmd.SeqCmd:X2}");
            return true;
        }

        public async Task<bool> LOKE_Initialize(device device, long totalFileSize)
        {
            SamsungLokeCommand command = new SamsungLokeCommand(0x64, 0, 5L);
            if (await LOKE_SendCMD(device, command))
            {
                LokeVariant = GetVariant(BufferRead);
                if (LokeVariant == 5L)
                {
                    command = new SamsungLokeCommand(0x64, 12);
                    await LOKE_SendCMD(device, command, false);
                }

                if (totalFileSize != 0)
                    return await LOKE_SetFlashTotal(device, totalFileSize);
            }
            return true;
        }

        /// <summary>
        /// cmd 0x64/0x02 — total del lote para la barra blanca en Download Mode.
        /// Debe coincidir con Calculated Size (BL+AP+CP+CSC), no solo con el subconjunto PIT.
        /// Ref. Thor_Flash SetTotalBytes / Heimdall PR #459.
        /// </summary>
        public async Task<bool> LOKE_SetFlashTotal(device device, long totalFileSize)
        {
            if (totalFileSize <= 0)
                return true;

            SamsungLokeCommand command;
            if (LokeVariant == 2L)
            {
                command = new SamsungLokeCommand(0x64, 2);
                await LOKE_SendCMD(device, command);
            }

            if (LokeVariant == 3L || LokeVariant == 4L || LokeVariant == 5L)
            {
                int packetBytes = Odin.LokeSessionPacketBytes;
                if (packetBytes < 4096)
                    packetBytes = 1048576;
                if (packetBytes > 1048576)
                    packetBytes = 1048576;
                command = new SamsungLokeCommand(0x64, 5, packetBytes);
                await LOKE_SendCMD(device, command);
            }

            uint lo = (uint)(totalFileSize & 0xFFFFFFFFL);
            int hi = (int)(totalFileSize >> 32); // Obligatorio si total > 4 GiB (ej. AP ~8 GB)
            command = new SamsungLokeCommand(0x64, 2, lo, hi);
            await LOKE_SendCMD(device, command);

            if (LokeVariant == 4L)
            {
                int i = 0;
                while (i < 3)
                {
                    command = new SamsungLokeCommand(0x69, i);
                    await LOKE_SendCMD(device, command);
                    i++;
                }
            }

            return true;
        }

    }
}
