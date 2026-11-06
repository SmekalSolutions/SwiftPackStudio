using System.Windows;

namespace SwiftPack.Views
{
    public partial class NoFilesInFolderDialog : Window
    {
        public NoFilesInFolderDialog(string folderPath)
        {
            InitializeComponent();
            TxtFolderPath.Text = folderPath;
        }

        private void TitleBar_MouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
            => DragMove();

        private void BtnClose_Click(object sender, RoutedEventArgs e)
            => Close();
    }
}
