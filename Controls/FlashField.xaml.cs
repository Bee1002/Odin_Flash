using Odin_Flash.Util;
using Microsoft.Win32;
using OdinProtocolAtack;
using OdinProtocolAtack.util;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;

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

            // Mostrar solo el icono de carpeta (ya definido en XAML) y el texto corto del slot (ej. "BL")
            BtnChooseFile.Content = PackageSlot;
            // Placeholder reducido a solo el slot (ej. "BL")
            txtSelectTeam.Text = PackageSlot;
            view.Refresh();
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
                Filter = "samsung firmware|*.tar;*.md5;*.limra"
            };
            bool? result = dlg.ShowDialog();
            if (result == true)
            {
                BtnClear_Click(sender, e);
                string filename = dlg.FileName;
                var odin = new Odin();
                var item = odin.tar.TarInformation(filename);
                if (item.Count > 0)
                {
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
                                FilePath = filename
                            };

                            if (Extension == ".lz4")
                            {
                                file.RawSize = odin.CalculateLz4SizeFromTar(filename, Tiem.Filename);
                                if (file.RawSize <= 0)
                                    continue;
                            }
                            else
                            {
                                file.RawSize = Tiem.Filesize;
                            }
                            FlashFile.Add(file);
                        }
                    }
                    if (CmbBxListFile.Items.Count > 0)
                    {
                        BtnClear.Visibility = Visibility.Visible;
                        txtSelectTeam.Text = filename;
                    }
                    view.Refresh();

                }

            }
        }

        private void BtnClear_Click(object sender, RoutedEventArgs e)
        {
            FlashFile.Clear();
            view.Refresh();
            // Volver al texto corto del slot al limpiar
            txtSelectTeam.Text = PackageSlot;
            BtnClear.Visibility = Visibility.Collapsed;

        }

        private void CmbBxListFile_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            CmbBxListFile.SelectedItem = null;

        }
    }
}