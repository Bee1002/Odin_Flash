using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using OdinProtocolAtack;
using OdinProtocolAtack.structs;
using OdinProtocolAtack.util;
using static OdinProtocolAtack.util.utils;

namespace OdinProtocolAtack.test
{
    internal class Program
    {
        private static void Main(string[] args)
        {
        }

        private readonly Odin Odin = new Odin();

        public Program()
        {
            Odin.Log += Odin_Log;
            Odin.ProgressChanged += Odin_ProgressChanged;
        }

        private void Odin_Log(string text, MsgType color, bool isError = false)
        {
            throw new NotImplementedException();
        }

        private void Odin_ProgressChanged(string filename, long max, long value, long writenSize)
        {
        }

        public async Task FindOdin()
        {
            var device = await Odin.FindDownloadModePort();
            Console.WriteLine(device.Name);
            Console.WriteLine(device.COM);
            Console.WriteLine(device.VID);
            Console.WriteLine(device.PID);
        }

        public async Task ReadOdinInfo()
        {
            if (await Odin.FindAndSetDownloadMode())
            {
                await Odin.DVIF();
                await Odin.PrintInfo();
            }
        }

        public async Task ReadPit()
        {
            if (await Odin.FindAndSetDownloadMode())
            {
                await Odin.PrintInfo();
                if (await Odin.IsOdin())
                {
                    if (await Odin.LOKE_Initialize(0))
                    {
                        var pit = await Odin.Read_Pit();
                        if (pit.Result)
                        {
                            var buffer = pit.data;
                            var entry = pit.Pit;
                        }
                    }
                }
            }
        }

        /// <summary>
        /// write pit file on your device
        /// </summary>
        /// <param name="pit">in this parameter, you can set tar.md5 contains have pit file(Like csc package of firmware)
        /// or pit file with .pit format
        /// </param>
        /// <returns>true if success</returns>
        public async Task<bool> Write_Pit(string pit)
        {
            if (await Odin.FindAndSetDownloadMode())
            {
                await Odin.PrintInfo();
                if (await Odin.IsOdin())
                {
                    if (await Odin.LOKE_Initialize(0))
                    {
                        var pitResult = await Odin.Write_Pit(pit);
                        return pitResult.status;
                    }
                }
            }
            return false;
        }

        public bool Exist(cListFileData file, List<FileFlash> flashFile)
        {
            foreach (var item in flashFile)
            {
                if (item.FileName == file.Filename)
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>
        /// Add List Of Your tar package (bl,ap,cp,csc , or more)
        /// </summary>
        /// <param name="listOfTarFile">add tar type files path in this list</param>
        /// <returns></returns>
        public async Task<bool> FlashFirmware(List<string> listOfTarFile)
        {
            var flashFile = new List<FileFlash>();
            foreach (var i in listOfTarFile)
            {
                var item = Odin.tar.TarInformation(i);
                if (item == null || item.Count == 0)
                {
                    continue;
                }

                foreach (var tiem in item)
                {
                    if (!Exist(tiem, flashFile))
                    {
                        var extension = Path.GetExtension(tiem.Filename);
                        var file = new FileFlash
                        {
                            Enable = true,
                            FileName = tiem.Filename,
                            FilePath = i
                        };

                        if (extension == ".pit")
                        {
                        }
                        else if (extension == ".lz4")
                        {
                            file.RawSize = Odin.CalculateLz4SizeFromTar(i, tiem.Filename);
                        }
                        else
                        {
                            file.RawSize = tiem.Filesize;
                        }
                        flashFile.Add(file);
                    }
                }
            }

            if (flashFile.Count > 0)
            {
                var size = 0L;
                foreach (var item in flashFile)
                {
                    size += item.RawSize;
                }
                if (await Odin.FindAndSetDownloadMode())
                {
                    await Odin.PrintInfo();
                    if (await Odin.IsOdin())
                    {
                        if (await Odin.LOKE_Initialize(size))
                        {
                            var findPit = flashFile.Find(x => x.FileName.ToLower().EndsWith(".pit"));
                            if (findPit != null)
                            {
                                var res = MessageBox.Show("Pit Finded on your tar package , you want to repartition?", "", MessageBoxButton.YesNo);
                                if (res == MessageBoxResult.Yes)
                                {
                                    await Odin.Write_Pit(findPit.FilePath);
                                }
                            }

                            var readPit = await Odin.Read_Pit();
                            if (readPit.Result)
                            {
                                var efsClearInt = 0;
                                var bootUpdateInt = 1;
                                if (await Odin.FlashFirmware(flashFile, readPit.Pit, efsClearInt, bootUpdateInt, true))
                                {
                                    if (await Odin.PDAToNormal())
                                    {
                                        return true;
                                    }
                                }
                            }
                        }
                    }
                }
            }

            return false;
        }

        /// <summary>
        /// Flash Single File lz4 , image
        /// </summary>
        /// <param name="filePath">path of your file</param>
        /// <param name="partitionFileName">like boot.img , sboot.bin or more ...</param>
        /// <returns></returns>
        public async Task<bool> FlashSingleFile(string filePath, string partitionFileName)
        {
            var flashFile = new FileFlash
            {
                Enable = true,
                FileName = partitionFileName,
                FilePath = filePath,
                RawSize = new FileInfo(filePath).Length
            };

            if (await Odin.FindAndSetDownloadMode())
            {
                await Odin.PrintInfo();
                if (await Odin.IsOdin())
                {
                    if (await Odin.LOKE_Initialize(flashFile.RawSize))
                    {
                        var readPit = await Odin.Read_Pit();
                        if (readPit.Result)
                        {
                            var efsClearInt = 0;
                            var bootUpdateInt = 0;
                            if (await Odin.FlashSingleFile(flashFile, readPit.Pit, efsClearInt, bootUpdateInt, true))
                            {
                                if (await Odin.PDAToNormal())
                                {
                                    return true;
                                }
                            }
                        }
                    }
                }
            }

            return false;
        }
    }
}
