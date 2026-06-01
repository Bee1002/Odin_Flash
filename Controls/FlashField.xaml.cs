using Odin_Flash.Util;
using Microsoft.Win32;
using OdinFlash.Protocol;
using OdinFlash.Protocol.util;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;

namespace Odin_Flash.Controls
{
    /// <summary>
    /// Interaction logic for FlashField.xaml
    /// </summary>
    public partial class FlashField : UserControl
    {
        public List<FileFlash> FlashFile = new List<FileFlash>();
        public List<FileFlash> Files
        {
            get
            {
                if (FlashFile.Count <= 0)
                {
                    return FlashFile;
                }
                var items = CmbBxListFile.Items.OfType<FileFlash>().ToList();
                return items;
            }
        }

        public ListCollectionView view;
        public string Package;
        private readonly string PackageSlot;
        public FlashField(string package)
        {
            InitializeComponent();
            this.Package = package;
            PackageSlot = GetPackageSlot(package);
            view = new ListCollectionView(FlashFile);
            view.IsLiveFiltering = true;
            view.IsLiveSorting = true;
            CmbBxListFile.ItemsSource = view;
            BtnClear.Visibility = Visibility.Collapsed;

            TxtBrowseSlot.Text = PackageSlot;
            txtSelectTeam.Text = PackageSlot;
            if (IsKnownSlot(PackageSlot))
                ApplySlotAccent("SlotAccentBrush");
            view.Refresh();
        }

        private static bool IsKnownSlot(string slot)
        {
            return slot == "BL" || slot == "AP" || slot == "CP" || slot == "CSC";
        }

        private void ApplySlotAccent(string brushKey)
        {
            var accent = (Brush)FindResource(brushKey);
            txtSelectTeam.Foreground = accent;
            BtnChooseFile.Foreground = accent;
            BtnChooseFile.BorderBrush = accent;
        }

        private static string GetPackageSlot(string package)
        {
            if (package.StartsWith("BL", StringComparison.OrdinalIgnoreCase))
                return "BL";
            if (package.StartsWith("AP", StringComparison.OrdinalIgnoreCase))
                return "AP";
            if (package.StartsWith("CP", StringComparison.OrdinalIgnoreCase))
                return "CP";
            if (package.StartsWith("CSC", StringComparison.OrdinalIgnoreCase))
                return "CSC";

            return package;
        }

        public bool Exist(cListFileData File)
        {
            foreach (var item in FlashFile)
            {
                if (item.FileName == File.Filename)
                {
                    return true;
                }
            }
            return false;
        }

        private static bool IsFlashableTarEntry(cListFileData file)
        {
            if (file == null || string.IsNullOrWhiteSpace(file.Filename))
                return false;
            if (string.Equals(file.Filetype, "Directory", StringComparison.OrdinalIgnoreCase))
                return false;
            if (file.Filename.EndsWith("/", StringComparison.Ordinal))
                return false;
            if (file.Filename.StartsWith("meta-data/", StringComparison.OrdinalIgnoreCase))
                return false;

            var extension = System.IO.Path.GetExtension(file.Filename);
            return extension.Equals(".lz4", StringComparison.OrdinalIgnoreCase)
                || extension.Equals(".img", StringComparison.OrdinalIgnoreCase)
                || extension.Equals(".bin", StringComparison.OrdinalIgnoreCase)
                || extension.Equals(".mbn", StringComparison.OrdinalIgnoreCase)
                || extension.Equals(".elf", StringComparison.OrdinalIgnoreCase);
        }

        private void BtnChooseFile_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new OpenFileDialog
            {
                DefaultExt = ".tar",
                Filter = "samsung firmware|*.tar;*.md5"
            };
            bool? result = dlg.ShowDialog();
            if (result == true)
                TryLoadTarPackage(dlg.FileName);
        }

        /// <summary>Carga un TAR/md5 en este slot (Browse o drag-and-drop).</summary>
        public bool TryLoadTarPackage(string tarPath)
        {
            if (string.IsNullOrWhiteSpace(tarPath) || !System.IO.File.Exists(tarPath))
                return false;

            ClearPackage();
            var odin = new Odin();
            var item = odin.tar.TarInformation(tarPath);
            if (item.Count <= 0)
                return false;

            foreach (var Tiem in item)
            {
                if (!IsFlashableTarEntry(Tiem))
                    continue;

                if (!Exist(Tiem))
                {
                    var Extension = System.IO.Path.GetExtension(Tiem.Filename);
                    var file = new FileFlash
                    {
                        Enable = true,
                        FileName = Tiem.Filename,
                        FilePath = tarPath
                    };

                    if (Extension == ".lz4")
                    {
                        try
                        {
                            file.RawSize = odin.GetTarEntryFlashBytes(tarPath, Tiem.Filename);
                        }
                        catch
                        {
                            file.RawSize = odin.CalculateLz4SizeFromTar(tarPath, Tiem.Filename);
                        }
                        if (file.RawSize <= 0)
                            continue;
                    }
                    else
                    {
                        try
                        {
                            file.RawSize = odin.GetTarEntryFlashBytes(tarPath, Tiem.Filename);
                        }
                        catch
                        {
                            file.RawSize = Tiem.Filesize;
                        }
                    }
                    FlashFile.Add(file);
                }
            }

            if (CmbBxListFile.Items.Count <= 0)
                return false;

            BtnClear.Visibility = Visibility.Visible;
            txtSelectTeam.Text = tarPath;
            view.Refresh();
            return true;
        }

        public void ClearPackage()
        {
            FlashFile.Clear();
            view.Refresh();
            txtSelectTeam.Text = PackageSlot;
            BtnClear.Visibility = Visibility.Collapsed;
        }

        private void BtnClear_Click(object sender, RoutedEventArgs e)
        {
            ClearPackage();
        }

        private void CmbBxListFile_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            CmbBxListFile.SelectedItem = null;

        }
    }
}