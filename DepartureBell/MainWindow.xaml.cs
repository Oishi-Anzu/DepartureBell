
using Microsoft.Win32;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using System;
using System.Collections.ObjectModel;
using System.IO;
using System.IO.Ports;
using System.Linq;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace DepartureBell
{
    public partial class MainWindow : Window
    {
        // =====================================
        // 1. フィールド・変数
        // =====================================

        // 音源一覧
        private ObservableCollection<AudioFileItem> melodies = new();
        private ObservableCollection<AudioFileItem> doorAnnouncements = new();

        // アプリの保存先
        private readonly string appFolder =
            Path.Combine(
                Environment.GetFolderPath(
                    Environment.SpecialFolder.LocalApplicationData),
                "DepartureBell");

        // COMポート設定ファイル
        private readonly string serialConfigPath =
            Path.Combine(
                Environment.GetFolderPath(
                    Environment.SpecialFolder.LocalApplicationData),
                "DepartureBell",
                "serial-port.txt");

        // 音源保存先
        private string MelodyFolder =>
            Path.Combine(appFolder, "Audio", "Melodies");

        private string DoorFolder =>
            Path.Combine(appFolder, "Audio", "DoorAnnouncements");

        // 登録情報保存ファイル
        private string LibraryFile =>
            Path.Combine(appFolder, "library.json");

        // シリアル通信
        private SerialPort? serialPort;
        private DispatcherTimer? reconnectTimer;
        private DispatcherTimer? dsrTimer;
        private bool isUpdatingSerialPorts = false;

        // メロディ再生
        private IWavePlayer? melodyOut;
        private WaveStream? melodyReader;
        private LoopStream? melodyLoopStream;
        private VolumeSampleProvider? melodyVolumeProvider;

        // 戸閉放送再生
        private IWavePlayer? doorOut;
        private WaveStream? doorReader;

        // 現在再生中の音源
        private AudioFileItem? currentMelody;
        private AudioFileItem? currentDoor;

        // 戸閉放送中にメロディを重ねるか
        private bool overlayDoorAnnouncement = false;

        // メロディ終了後に戸閉放送を再生するか
        private bool playDoorAfterMelody = false;

        // 新幹線式
        private DispatcherTimer? shinkansenTimer;
        private AudioFileItem? pendingShinkansenDoor;
        private float originalMelodyVolume;


        // =====================================
        // 2. コンストラクター・初期化
        // =====================================

        public MainWindow()
        {
            InitializeComponent();

            // 保存フォルダ作成
            Directory.CreateDirectory(MelodyFolder);
            Directory.CreateDirectory(DoorFolder);

            // 音源一覧読み込み
            LoadLibrary();

            // 一覧と画面を接続
            MelodyListBox.ItemsSource = melodies;
            DoorAnnouncementListBox.ItemsSource = doorAnnouncements;

            MelodyComboBox.ItemsSource = melodies;

            SettingsMelodyComboBox.ItemsSource = melodies;
            MelodyDoorAnnouncementComboBox.ItemsSource =
                doorAnnouncements;

            // 動作設定を画面に反映
            OverlayDoorMelodyRadio.IsChecked =
                overlayDoorAnnouncement;

            StopDoorBeforeMelodyRadio.IsChecked =
                !overlayDoorAnnouncement;

            // シリアル通信初期化
            InitializeSerialConnection();
        }


        // =====================================
        // 3. 画面切り替え
        // =====================================

        private void ShowOperation_Click(
            object sender,
            RoutedEventArgs e)
        {
            OperationPanel.Visibility = Visibility.Visible;
            ManagementPanel.Visibility = Visibility.Collapsed;
        }

        private void ShowManagement_Click(
            object sender,
            RoutedEventArgs e)
        {
            OperationPanel.Visibility = Visibility.Collapsed;
            ManagementPanel.Visibility = Visibility.Visible;
        }


        // =====================================
        // 4. シリアル通信
        // =====================================

        // シリアル通信初期化
        private void InitializeSerialConnection()
        {
            Directory.CreateDirectory(
                Path.GetDirectoryName(serialConfigPath)!);

            string savedPort = "";

            if (File.Exists(serialConfigPath))
            {
                savedPort = File.ReadAllText(serialConfigPath).Trim();
            }

            RefreshSerialPortList(savedPort);

            // 自動再接続タイマー
            reconnectTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(2)
            };

            reconnectTimer.Tick += ReconnectTimer_Tick;
            reconnectTimer.Start();

            // 保存済みポートに接続
            if (SerialPortComboBox.SelectedItem != null)
            {
                ConnectSerial();
            }
        }


        // COMポート一覧更新
        private void RefreshSerialPortList(
            string? preferredPort = null)
        {
            isUpdatingSerialPorts = true;

            try
            {
                string selectedPort =
                    preferredPort ??
                    SerialPortComboBox.SelectedItem?.ToString() ??
                    "";

                SerialPortComboBox.Items.Clear();

                string[] ports = SerialPort.GetPortNames();

                Array.Sort(ports);

                foreach (string port in ports)
                {
                    SerialPortComboBox.Items.Add(port);
                }

                // 保存済みポートが見つからなくても選択を維持
                if (!string.IsNullOrEmpty(selectedPort))
                {
                    if (!SerialPortComboBox.Items.Contains(selectedPort))
                    {
                        SerialPortComboBox.Items.Add(selectedPort);
                    }

                    SerialPortComboBox.SelectedItem = selectedPort;
                }
                else if (ports.Length > 0)
                {
                    SerialPortComboBox.SelectedIndex = 0;
                }
            }
            finally
            {
                isUpdatingSerialPorts = false;
            }
        }


        // COMポート一覧更新ボタン
        private void RefreshSerialPorts_Click(
            object sender,
            RoutedEventArgs e)
        {
            RefreshSerialPortList();

            if (SerialPortComboBox.SelectedItem != null)
            {
                ConnectSerial();
            }
        }


        // COMポート選択変更
        private void SerialPortComboBox_SelectionChanged(
            object sender,
            SelectionChangedEventArgs e)
        {
            if (isUpdatingSerialPorts)
                return;

            if (SerialPortComboBox.SelectedItem == null)
                return;

            string portName =
                SerialPortComboBox.SelectedItem.ToString()!;

            File.WriteAllText(serialConfigPath, portName);

            ConnectSerial();
        }


        // シリアル接続
        private void ConnectSerial()
        {
            try
            {
                DisconnectSerial();

                if (SerialPortComboBox.SelectedItem == null)
                {
                    SerialStatusText.Text = "接続待機中";
                    DsrStatusText.Text = "DSR：未確認";
                    return;
                }

                string portName =
                    SerialPortComboBox.SelectedItem.ToString()!;

                serialPort = new SerialPort(portName, 9600);

                serialPort.Open();

                // DSR監視タイマー
                dsrTimer = new DispatcherTimer
                {
                    Interval = TimeSpan.FromMilliseconds(100)
                };

                dsrTimer.Tick += DsrTimer_Tick;
                dsrTimer.Start();

                SerialStatusText.Text = $"{portName} 接続中";
                DsrStatusText.Text = "DSR：監視中";
            }
            catch (Exception)
            {
                DisconnectSerial();

                SerialStatusText.Text = "接続待機中";
                DsrStatusText.Text = "DSR：未確認";
            }
        }


        // DSR状態監視
        private void DsrTimer_Tick(
            object? sender,
            EventArgs e)
        {
            try
            {
                if (serialPort == null || !serialPort.IsOpen)
                    return;

                bool dsr = serialPort.DsrHolding;

                DsrStatusText.Text =
                    dsr ? "DSR：ON" : "DSR：OFF";
            }
            catch (Exception)
            {
                DisconnectSerial();

                SerialStatusText.Text = "接続待機中";
                DsrStatusText.Text = "DSR：未確認";
            }
        }


        // 自動再接続
        private void ReconnectTimer_Tick(
            object? sender,
            EventArgs e)
        {
            if (serialPort != null && serialPort.IsOpen)
                return;

            RefreshSerialPortList();

            if (SerialPortComboBox.SelectedItem != null)
            {
                ConnectSerial();
            }
        }


        // シリアル切断
        private void DisconnectSerial()
        {
            dsrTimer?.Stop();

            if (dsrTimer != null)
            {
                dsrTimer.Tick -= DsrTimer_Tick;
                dsrTimer = null;
            }

            if (serialPort != null)
            {
                try
                {
                    if (serialPort.IsOpen)
                    {
                        serialPort.Close();
                    }
                }
                catch (Exception)
                {
                    // 切断時の例外を処理
                }
                finally
                {
                    serialPort.Dispose();
                    serialPort = null;
                }
            }
        }


        // =====================================
        // 5. メロディ設定
        // =====================================

        // メロディ選択時に設定を読み込む
        private void SettingsMelodyComboBox_SelectionChanged(
            object sender,
            SelectionChangedEventArgs e)
        {
            if (SettingsMelodyComboBox.SelectedItem
                is not AudioFileItem item)
            {
                return;
            }

            // 音量
            MelodyVolumeSlider.Value =
                Math.Clamp(item.Volume, 0, 100);

            // 再生モード
            MelodyModeComboBox.SelectedIndex = item.Mode switch
            {
                "立川式" => 1,
                "西荻窪式" => 2,
                "新幹線式" => 3,
                _ => 0
            };

            // 戸閉放送
            MelodyDoorAnnouncementComboBox.SelectedItem =
                doorAnnouncements.FirstOrDefault(
                    door => door.FilePath ==
                    item.DoorAnnouncementFilePath);

            // 新幹線式設定
            ShinkansenDelayTextBox.Text =
                item.ShinkansenDelayMs.ToString();

            ShinkansenAttenuationTextBox.Text =
                item.ShinkansenAttenuationPercent.ToString();
        }


        // メロディ設定保存
        private void SaveMelodySettings_Click(
            object sender,
            RoutedEventArgs e)
        {
            if (SettingsMelodyComboBox.SelectedItem
                is not AudioFileItem item)
            {
                MessageBox.Show("設定するメロディを選択してください。");
                return;
            }

            // 新幹線式の設定を確認
            if (!int.TryParse(
                    ShinkansenDelayTextBox.Text,
                    out int delayMs) || delayMs < 0)
            {
                MessageBox.Show(
                    "戸閉放送開始時間は0以上の整数で入力してください。");
                return;
            }

            if (!double.TryParse(
                    ShinkansenAttenuationTextBox.Text,
                    out double attenuation) ||
                attenuation < 0 || attenuation > 100)
            {
                MessageBox.Show(
                    "減衰率は0～100の範囲で入力してください。");
                return;
            }

            // 音量
            item.Volume = MelodyVolumeSlider.Value;

            // 再生モード
            if (MelodyModeComboBox.SelectedItem
                is ComboBoxItem mode)
            {
                item.Mode = mode.Content?.ToString() ?? "通常";
            }

            // 戸閉放送
            if (MelodyDoorAnnouncementComboBox.SelectedItem
                is AudioFileItem door)
            {
                item.DoorAnnouncementFilePath = door.FilePath;
            }
            else
            {
                item.DoorAnnouncementFilePath = "";
            }

            // 新幹線式設定
            item.ShinkansenDelayMs = delayMs;
            item.ShinkansenAttenuationPercent = attenuation;

            SaveLibrary();

            MessageBox.Show(
                $"「{item.Name}」の設定を保存しました。",
                "保存完了",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }


        // 戸閉放送中の動作設定保存
        private void SavePlaybackSettings_Click(
            object sender,
            RoutedEventArgs e)
        {
            overlayDoorAnnouncement =
                OverlayDoorMelodyRadio.IsChecked == true;

            SaveLibrary();

            MessageBox.Show(
                "動作設定を保存しました。",
                "保存完了",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }


        // =====================================
        // 6. 音源管理
        // =====================================

        // メロディ追加
        private void AddMelody_Click(
            object sender,
            RoutedEventArgs e)
        {
            OpenAudioFile(melodies, MelodyFolder);
        }


        // 戸閉放送追加
        private void AddDoorAnnouncement_Click(
            object sender,
            RoutedEventArgs e)
        {
            OpenAudioFile(
                doorAnnouncements,
                DoorFolder);
        }


        // 音声ファイル追加
        private void OpenAudioFile(
            ObservableCollection<AudioFileItem> list,
            string destinationFolder)
        {
            OpenFileDialog dialog = new OpenFileDialog
            {
                Title = "音声ファイルを選択",
                Filter =
                    "音声ファイル|*.wav;*.mp3;*.wma|すべてのファイル|*.*",
                Multiselect = true
            };

            if (dialog.ShowDialog() != true)
                return;

            try
            {
                foreach (string sourceFile in dialog.FileNames)
                {
                    // 重複確認
                    bool exists = list.Any(
                        item => string.Equals(
                            item.OriginalPath,
                            sourceFile,
                            StringComparison.OrdinalIgnoreCase));

                    if (exists)
                        continue;

                    string extension =
                        Path.GetExtension(sourceFile);

                    string newFileName =
                        Guid.NewGuid().ToString("N") + extension;

                    string destinationFile =
                        Path.Combine(
                            destinationFolder,
                            newFileName);

                    // コピー
                    File.Copy(sourceFile, destinationFile);

                    // 登録
                    AudioFileItem newItem = new AudioFileItem
                    {
                        Name = Path.GetFileNameWithoutExtension(sourceFile),
                        FilePath = destinationFile,
                        OriginalPath = sourceFile
                    };

                    list.Add(newItem);
                }

                SaveLibrary();
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    "音源の追加に失敗しました。\n\n" + ex.Message,
                    "追加エラー",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
        }


        // メロディ削除
        private void DeleteMelody_Click(
            object sender,
            RoutedEventArgs e)
        {
            if (MelodyListBox.SelectedItem
                is AudioFileItem item)
            {
                DeleteAudioFile(item, melodies);
            }
            else
            {
                MessageBox.Show(
                    "削除するメロディを選択してください。");
            }
        }

        // =====================================
        // 音源の名前変更
        // =====================================

        // 発車メロディの名前変更
        private void RenameMelody_Click(
            object sender,
            RoutedEventArgs e)
        {
            if (MelodyListBox.SelectedItem is not AudioFileItem item)
            {
                MessageBox.Show("名前を変更するメロディを選択してください。");
                return;
            }

            RenameAudioItem(item, MelodyListBox, "発車メロディ");
        }


        // 戸閉放送の名前変更
        private void RenameDoorAnnouncement_Click(
            object sender,
            RoutedEventArgs e)
        {
            if (DoorAnnouncementListBox.SelectedItem is not AudioFileItem item)
            {
                MessageBox.Show("名前を変更する戸閉放送を選択してください。");
                return;
            }

            RenameAudioItem(item, DoorAnnouncementListBox, "戸閉放送");
        }


        // 名前変更の共通処理
        private void RenameAudioItem(
            AudioFileItem item,
            ListBox listBox,
            string itemType)
        {
            // 名前入力用ウィンドウ
            Window dialog = new Window
            {
                Title = itemType + "の名前変更",
                Width = 350,
                Height = 160,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Owner = this,
                ResizeMode = ResizeMode.NoResize
            };

            StackPanel panel = new StackPanel
            {
                Margin = new Thickness(15)
            };

            TextBox nameBox = new TextBox
            {
                Text = item.Name,
                Margin = new Thickness(0, 5, 0, 15),
                Height = 25
            };

            Button confirmButton = new Button
            {
                Content = "変更する",
                Width = 100,
                Height = 30,
                HorizontalAlignment = HorizontalAlignment.Right,
                IsDefault = true
            };

            confirmButton.Click += (s, args) =>
            {
                string newName = nameBox.Text.Trim();

                if (string.IsNullOrWhiteSpace(newName))
                {
                    MessageBox.Show(
                        "名前を入力してください。",
                        "入力エラー",
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning);

                    return;
                }

                // 名前を変更
                item.Name = newName;

                // ListBoxの表示を更新
                listBox.Items.Refresh();

                // JSONに保存
                SaveLibrary();

                dialog.DialogResult = true;
                dialog.Close();
            };

            panel.Children.Add(new TextBlock
            {
                Text = "新しい名前を入力してください。"
            });

            panel.Children.Add(nameBox);
            panel.Children.Add(confirmButton);

            dialog.Content = panel;

            dialog.Loaded += (s, args) =>
            {
                nameBox.Focus();
                nameBox.SelectAll();
            };

            dialog.ShowDialog();
        }


        // 戸閉放送削除
        private void DeleteDoorAnnouncement_Click(
            object sender,
            RoutedEventArgs e)
        {
            if (DoorAnnouncementListBox.SelectedItem
                is AudioFileItem item)
            {
                DeleteAudioFile(item, doorAnnouncements);
            }
            else
            {
                MessageBox.Show(
                    "削除する戸閉放送を選択してください。");
            }
        }


        // 音源削除共通処理
        private void DeleteAudioFile(
            AudioFileItem item,
            ObservableCollection<AudioFileItem> list)
        {
            MessageBoxResult result = MessageBox.Show(
                $"「{item.Name}」を削除しますか？\n\n" +
                "アプリに保存した音源ファイルも削除されます。",
                "音源の削除",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);

            if (result != MessageBoxResult.Yes)
                return;

            try
            {
                // 再生中なら停止
                if (currentMelody == item)
                {
                    StopMelodyPlayback();
                }

                if (currentDoor == item)
                {
                    StopDoorPlayback();
                }

                // メロディに設定されている戸閉放送の参照を解除
                foreach (AudioFileItem melody in melodies)
                {
                    if (melody.DoorAnnouncementFilePath == item.FilePath)
                    {
                        melody.DoorAnnouncementFilePath = "";
                    }
                }

                // 一覧から削除
                list.Remove(item);

                // 操作画面の選択解除
                if (MelodyComboBox.SelectedItem == item)
                {
                    MelodyComboBox.SelectedItem = null;
                }

                // 設定画面の選択解除
                if (SettingsMelodyComboBox.SelectedItem == item)
                {
                    SettingsMelodyComboBox.SelectedItem = null;
                }

                if (MelodyDoorAnnouncementComboBox.SelectedItem == item)
                {
                    MelodyDoorAnnouncementComboBox.SelectedItem = null;
                }

                // ファイル削除
                if (File.Exists(item.FilePath))
                {
                    File.Delete(item.FilePath);
                }

                PlaybackStatus.Text = "再生状態：停止中";

                SaveLibrary();
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    "音源の削除に失敗しました。\n\n" + ex.Message,
                    "削除エラー",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
        }


        // =====================================
        // 7. 音声再生操作
        // =====================================

        // 再生ボタン
        private void PlayMelody_Click(
            object sender,
            RoutedEventArgs e)
        {
            if (MelodyComboBox.SelectedItem
                is AudioFileItem item)
            {
                PlayMelodyAudio(item);
            }
            else
            {
                MessageBox.Show(
                    "発車メロディを選択してください。");
            }
        }


        // メロディに設定された戸閉放送を取得
        private AudioFileItem? GetDoorAnnouncement(
            AudioFileItem melody)
        {
            return doorAnnouncements.FirstOrDefault(
                item => string.Equals(
                    item.FilePath,
                    melody.DoorAnnouncementFilePath,
                    StringComparison.OrdinalIgnoreCase));
        }


        // 停止ボタン
        private void StopAudio_Click(
            object sender,
            RoutedEventArgs e)
        {
            if (MelodyComboBox.SelectedItem
                is not AudioFileItem melody)
            {
                StopPlayback();

                PlaybackStatus.Text = "再生状態：停止中";

                MessageBox.Show(
                    "発車メロディを選択してください。");

                return;
            }

            AudioFileItem? door =
                GetDoorAnnouncement(melody);

            if (door == null)
            {
                StopPlayback();

                PlaybackStatus.Text = "再生状態：停止中";

                MessageBox.Show(
                    "このメロディには戸閉放送が設定されていません。\n\n" +
                    "管理画面から戸閉放送を設定してください。");

                return;
            }

            switch (melody.Mode)
            {
                // 通常
                case "通常":

                    StopMelodyPlayback();
                    PlayDoorAudio(door);

                    break;


                // 立川式
                case "立川式":

                    if (melodyLoopStream != null)
                    {
                        melodyLoopStream.StopAfterCurrentLoop = true;
                    }

                    PlayDoorAudio(door);

                    break;


                // 西荻窪式
                case "西荻窪式":

                    playDoorAfterMelody = true;

                    if (melodyLoopStream != null)
                    {
                        melodyLoopStream.StopAfterCurrentLoop = true;
                    }
                    else
                    {
                        playDoorAfterMelody = false;
                        PlayDoorAudio(door);
                    }

                    break;


                // 新幹線式
                case "新幹線式":

                    if (shinkansenTimer != null)
                    {
                        StopMelodyPlayback(false);

                        PlaybackStatus.Text =
                            "再生状態：戸閉放送開始待ち";
                    }
                    else
                    {
                        StopMelodyPlayback();

                        if (doorOut != null)
                        {
                            PlaybackStatus.Text =
                                "再生状態：戸閉放送再生中";
                        }
                        else
                        {
                            PlaybackStatus.Text =
                                "再生状態：停止中";
                        }
                    }

                    break;


                // その他は通常動作
                default:

                    StopMelodyPlayback();
                    PlayDoorAudio(door);

                    break;
            }
        }


        // 音声ファイルを開く
        private WaveStream OpenAudioReader(string filePath)
        {
            if (!File.Exists(filePath))
            {
                throw new FileNotFoundException(
                    "音源ファイルが見つかりません。",
                    filePath);
            }

            string extension =
                Path.GetExtension(filePath).ToLowerInvariant();

            if (extension == ".wma")
            {
                return new MediaFoundationReader(filePath);
            }

            return new AudioFileReader(filePath);
        }


        // =====================================
        // 8. メロディ再生
        // =====================================

        private void PlayMelodyAudio(AudioFileItem item)
        {
            try
            {
                // 既存のメロディを停止
                StopMelodyPlayback();

                // 戸閉放送中の動作
                if (!overlayDoorAnnouncement)
                {
                    StopDoorPlayback();
                }

                melodyReader =
                    OpenAudioReader(item.FilePath);

                melodyLoopStream =
                    new LoopStream(melodyReader);

                melodyVolumeProvider =
                    new VolumeSampleProvider(
                        melodyLoopStream.ToSampleProvider());

                // 音量設定
                originalMelodyVolume =
                    (float)(Math.Clamp(item.Volume, 0, 100) / 100.0);

                melodyVolumeProvider.Volume =
                    originalMelodyVolume;

                // 再生準備
                melodyOut = new WaveOut();

                melodyOut.PlaybackStopped +=
                    MelodyPlaybackStopped;

                melodyOut.Init(melodyVolumeProvider);

                currentMelody = item;

                // 新幹線式の戸閉放送を予約
                pendingShinkansenDoor =
                    GetDoorAnnouncement(item);

                // 再生開始
                melodyOut.Play();

                PlaybackStatus.Text =
                    "再生状態：メロディ繰り返し再生中 - " +
                    item.Name;

                // 新幹線式タイマー
                if (item.Mode == "新幹線式")
                {
                    shinkansenTimer = new DispatcherTimer
                    {
                        Interval = TimeSpan.FromMilliseconds(
                            Math.Max(0, item.ShinkansenDelayMs))
                    };

                    shinkansenTimer.Tick +=
                        ShinkansenTimer_Tick;

                    shinkansenTimer.Start();
                }
            }
            catch (Exception ex)
            {
                StopMelodyPlayback();

                MessageBox.Show(
                    "メロディを再生できませんでした。\n\n" +
                    ex.Message,
                    "再生エラー",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
        }


        // =====================================
        // 9. 新幹線式処理
        // =====================================

        private void ShinkansenTimer_Tick(
            object? sender,
            EventArgs e)
        {
            // タイマー停止
            shinkansenTimer?.Stop();

            if (shinkansenTimer != null)
            {
                shinkansenTimer.Tick -= ShinkansenTimer_Tick;
                shinkansenTimer = null;
            }

            // 戸閉放送取得
            AudioFileItem? door =
                pendingShinkansenDoor;

            pendingShinkansenDoor = null;

            if (door == null)
                return;

            // メロディ音量を減衰
            if (melodyOut != null &&
                melodyVolumeProvider != null &&
                currentMelody?.Mode == "新幹線式")
            {
                double attenuation =
                    Math.Clamp(
                        currentMelody.ShinkansenAttenuationPercent,
                        0,
                        100);

                melodyVolumeProvider.Volume =
                    originalMelodyVolume *
                    (float)(1.0 - attenuation / 100.0);
            }

            // 戸閉放送再生
            PlayDoorAudio(door);
        }


        // =====================================
        // 10. 戸閉放送再生
        // =====================================

        private void PlayDoorAudio(AudioFileItem item)
        {
            try
            {
                StopDoorPlayback();

                doorReader =
                    OpenAudioReader(item.FilePath);

                doorOut = new WaveOut();

                doorOut.PlaybackStopped +=
                    DoorPlaybackStopped;

                doorOut.Init(doorReader);

                currentDoor = item;

                doorOut.Play();

                PlaybackStatus.Text =
                    "再生状態：戸閉放送再生中 - " +
                    item.Name;
            }
            catch (Exception ex)
            {
                StopDoorPlayback();

                MessageBox.Show(
                    "戸閉放送を再生できませんでした。\n\n" +
                    ex.Message,
                    "再生エラー",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
        }


        // =====================================
        // 11. 再生終了・停止処理
        // =====================================

        // メロディ再生終了時
        private void MelodyPlaybackStopped(
            object? sender,
            StoppedEventArgs e)
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                bool shouldPlayDoor =
                    playDoorAfterMelody;

                AudioFileItem? finishedMelody =
                    currentMelody;

                playDoorAfterMelody = false;

                CleanupMelodyPlayback();

                // 西荻窪式：メロディ終了後に戸閉放送
                if (shouldPlayDoor &&
                    finishedMelody != null)
                {
                    AudioFileItem? door =
                        GetDoorAnnouncement(finishedMelody);

                    if (door != null)
                    {
                        PlayDoorAudio(door);
                        return;
                    }
                }

                if (doorOut == null)
                {
                    PlaybackStatus.Text =
                        "再生状態：停止中";
                }
            }));
        }


        // 戸閉放送再生終了時
        private void DoorPlaybackStopped(
            object? sender,
            StoppedEventArgs e)
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                CleanupDoorPlayback();

                // 新幹線式：音量を元に戻す
                if (melodyOut != null &&
                    melodyVolumeProvider != null &&
                    currentMelody?.Mode == "新幹線式")
                {
                    melodyVolumeProvider.Volume =
                        originalMelodyVolume;
                }

                if (melodyOut == null)
                {
                    PlaybackStatus.Text =
                        "再生状態：停止中";
                }
                else
                {
                    PlaybackStatus.Text =
                        "再生状態：メロディ再生中";
                }
            }));
        }


        // メロディ停止
        private void StopMelodyPlayback(
            bool stopShinkansenTimer = true)
        {
            if (stopShinkansenTimer)
            {
                shinkansenTimer?.Stop();

                if (shinkansenTimer != null)
                {
                    shinkansenTimer.Tick -= ShinkansenTimer_Tick;
                    shinkansenTimer = null;
                }

                pendingShinkansenDoor = null;
            }

            if (melodyOut != null)
            {
                melodyOut.PlaybackStopped -=
                    MelodyPlaybackStopped;

                melodyOut.Stop();
            }

            CleanupMelodyPlayback();
        }


        // 戸閉放送停止
        private void StopDoorPlayback()
        {
            if (doorOut != null)
            {
                doorOut.PlaybackStopped -=
                    DoorPlaybackStopped;

                doorOut.Stop();
            }

            CleanupDoorPlayback();
        }


        // メロディ再生オブジェクト後片付け
        private void CleanupMelodyPlayback()
        {
            melodyOut?.Dispose();
            melodyOut = null;

            melodyLoopStream?.Dispose();
            melodyLoopStream = null;

            melodyReader = null;
            melodyVolumeProvider = null;

            currentMelody = null;
        }


        // 戸閉放送再生オブジェクト後片付け
        private void CleanupDoorPlayback()
        {
            doorOut?.Dispose();
            doorOut = null;

            doorReader?.Dispose();
            doorReader = null;

            currentDoor = null;
        }


        // すべての再生を停止
        private void StopPlayback()
        {
            StopMelodyPlayback();
            StopDoorPlayback();

            playDoorAfterMelody = false;
            pendingShinkansenDoor = null;
        }


        // =====================================
        // 12. 設定保存・読み込み
        // =====================================

        // 登録内容をJSONに保存
        private void SaveLibrary()
        {
            try
            {
                AudioLibrary library = new AudioLibrary
                {
                    Melodies = melodies.ToList(),
                    DoorAnnouncements = doorAnnouncements.ToList(),
                    OverlayDoorAnnouncement =
                        overlayDoorAnnouncement
                };

                string json =
                    JsonSerializer.Serialize(
                        library,
                        new JsonSerializerOptions
                        {
                            WriteIndented = true
                        });

                Directory.CreateDirectory(appFolder);

                File.WriteAllText(LibraryFile, json);
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    "登録内容の保存に失敗しました。\n\n" +
                    ex.Message,
                    "保存エラー",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
        }


        // JSONから登録内容を読み込む
        private void LoadLibrary()
        {
            if (!File.Exists(LibraryFile))
                return;

            try
            {
                string json =
                    File.ReadAllText(LibraryFile);

                AudioLibrary? library =
                    JsonSerializer.Deserialize<AudioLibrary>(json);

                if (library == null)
                    return;

                overlayDoorAnnouncement =
                    library.OverlayDoorAnnouncement;

                melodies.Clear();
                doorAnnouncements.Clear();

                foreach (AudioFileItem item in library.Melodies)
                {
                    melodies.Add(item);
                }

                foreach (AudioFileItem item in library.DoorAnnouncements)
                {
                    doorAnnouncements.Add(item);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    "登録内容の読み込みに失敗しました。\n\n" +
                    ex.Message,
                    "読み込みエラー",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
        }


        // =====================================
        // 13. アプリ終了処理
        // =====================================

        private void MainWindow_Closing(
            object? sender,
            System.ComponentModel.CancelEventArgs e)
        {
            // 自動再接続停止
            reconnectTimer?.Stop();

            if (reconnectTimer != null)
            {
                reconnectTimer.Tick -= ReconnectTimer_Tick;
                reconnectTimer = null;
            }

            // 音声再生停止
            StopPlayback();

            // シリアル通信切断
            DisconnectSerial();
        }
    }


    // =====================================
    // 14. 音源情報クラス
    // =====================================

    public class AudioFileItem
    {
        public string Name { get; set; } = "";

        public string FilePath { get; set; } = "";

        public string OriginalPath { get; set; } = "";

        // 音量（0～100）
        public double Volume { get; set; } = 100;

        // 再生モード
        public string Mode { get; set; } = "通常";

        // 停止時に再生する戸閉放送
        public string DoorAnnouncementFilePath { get; set; } = "";

        // 新幹線式：戸閉放送開始までの時間
        public int ShinkansenDelayMs { get; set; } = 5000;

        // 新幹線式：メロディ音量の減衰率
        public double ShinkansenAttenuationPercent { get; set; } = 40;
    }


    // =====================================
    // 15. 音源一覧・設定保存クラス
    // =====================================

    public class AudioLibrary
    {
        public System.Collections.Generic.List<AudioFileItem> Melodies
        { get; set; } = new();

        public System.Collections.Generic.List<AudioFileItem> DoorAnnouncements
        { get; set; } = new();

        // 戸閉放送中にメロディを重ねるか
        public bool OverlayDoorAnnouncement { get; set; } = false;
    }


    // =====================================
    // 16. メロディ繰り返し再生クラス
    // =====================================

    public class LoopStream : WaveStream
    {
        private readonly WaveStream sourceStream;

        // trueの場合、現在のループ終了後に停止
        public bool StopAfterCurrentLoop { get; set; } = false;

        public LoopStream(WaveStream sourceStream)
        {
            this.sourceStream = sourceStream;
        }

        public override WaveFormat WaveFormat =>
            sourceStream.WaveFormat;

        public override long Length =>
            long.MaxValue;

        public override long Position
        {
            get => sourceStream.Position;
            set => sourceStream.Position = value;
        }

        public override int Read(
            byte[] buffer,
            int offset,
            int count)
        {
            int totalBytesRead = 0;

            while (count > 0)
            {
                int bytesRead =
                    sourceStream.Read(buffer, offset, count);

                if (bytesRead == 0)
                {
                    // 次のループに進まず終了
                    if (StopAfterCurrentLoop)
                    {
                        break;
                    }

                    // 先頭に戻って繰り返す
                    sourceStream.Position = 0;

                    bytesRead =
                        sourceStream.Read(buffer, offset, count);

                    if (bytesRead == 0)
                    {
                        break;
                    }
                }

                offset += bytesRead;
                count -= bytesRead;
                totalBytesRead += bytesRead;
            }

            return totalBytesRead;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                sourceStream.Dispose();
            }

            base.Dispose(disposing);
        }
    }
}
