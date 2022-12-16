using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO.Ports;
using System.Linq;
using System.Management;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Threading;

using MccDaq;
using OxyPlot.Series;
using OxyPlot;
using FontWeights = System.Windows.FontWeights;
using System.Collections.ObjectModel;
using OxyPlot.Axes;
using Microsoft.Win32;
using System.Globalization;

namespace LuminFe_v2
{
    /// <summary>
    /// Logique d'interaction pour MainWindow.xaml
    /// </summary>
    /// public class User
    public class DataSerie
    {
        public int number { get; set; }
        public List<DataPoint> dps { get; set; }
        public DataSerie(int i, string nom)
        {
            number = i+1;
            dps = new List<DataPoint>();
            Nom = nom;
        }

        public double MoyBasse { get; set; }
        public double MoyHaute { get; set; }
        public double Max { get; set; }
        public double Min { get; set; }
        public double Pente { get; set; }
        public double Integrale { get; set; }

        public int NbPoints { get; set; }
        public string Nom { get; set; }

        public DateTime heureDebut { get; set; }
        public DateTime heureFin { get; set; }

        public void process()
        {
            NbPoints = dps.Count();
            heureFin = DateTime.Now;
            if (NbPoints < 15) return;
            MoyBasse = 0;
            Min = Max = dps.ElementAt(0).Y;
            for (int i = 0; i < 15; i++)
            {
                MoyBasse += dps.ElementAt(i).Y/15;
            }
            for (int i = NbPoints - 15; i < NbPoints; i++)
            {
                MoyHaute += dps.ElementAt(i).Y/15;
            }

            Pente =(( MoyHaute / MoyBasse) - 1 )/ NbPoints;

            Integrale = 0;
            for (int i = 0; i < NbPoints; i++)
            {
                double pointCorrige = (dps.ElementAt(i).Y / MoyBasse) - Pente * i - 1;
                if (pointCorrige > 0) Integrale += pointCorrige;

                Max = dps.ElementAt(i).Y > Max ? dps.ElementAt(i).Y : Max;
                Min = dps.ElementAt(i).Y < Min ? dps.ElementAt(i).Y : Min;
            }

           

        }
    }


    public partial class MainWindow : Window, INotifyPropertyChanged
    {

        public GridViewColumnHeader _lastHeaderClicked = null;
        public ListSortDirection _lastDirection = ListSortDirection.Descending;

        public PlotModel MyModel { get; private set; }
        public event PropertyChangedEventHandler PropertyChanged;

        public ObservableCollection<DataSerie> dataSeries { get; } = new ObservableCollection<DataSerie>();

        SerialPort serialPM = new SerialPort();
        //public SerialMonitor serialMonitor;
        string received_data;
        string PM_ACK;
        string PMcommand;
        byte[] hexstring;

        byte byteCount = 0;



        int dpCounter = 0;

        bool COMPMConnected = false;


        MccDaq.ErrorInfo RetVal;

        ushort BitVal = 0;
        public const string DEVICE = "USB-1024LS"; public const int PORT = 10;

        int Vanne1Pos = 0;//HOME
        int Vanne2Pos = 0;//LOAD = 0, INJECT =1

        MccDaq.MccBoard daq;
        int BoardNum = 0;

        CancellationTokenSource ctsTic = new CancellationTokenSource();
        CancellationTokenSource ctsAcquisition = new CancellationTokenSource();


        int tempsPreRincage = 1;
        int tempsPompage = 1;
        int tempsRincage = 1;
        int tempsAcquisition = 30;
        int nbEchantillons = 0;

        int numeroEchantillon = 0;

        int timerEnCours;
        int timerRincage;

        bool cycleStepChange=false;
        bool cycleRunning = false;

        bool acquisitionEnCours = false;
        bool vannesConnected = false;

        enum cycleEnCours
        {
            IDLE,
            PRERINCAGE,
            POMPAGE,
            RINCAGE,
            ACQUISITION,
            FINITION
        }

        cycleEnCours CycleEnCours = cycleEnCours.IDLE;

        private void RaisePropertyChanged(string propertyName = "")
        {
            this.PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
        public MainWindow()
        {
            InitializeComponent();
            updatePortsList(comboBoxPortsPM);
            labelVannesStatus.Content = "Non connectées";
            labelPMStatus.Content = "Non connecté";
            initVannes();
            hexstring = new byte[] { 0,0,0,0};

            this.DataContext = this;
            this.MyModel = new PlotModel() { Title = "Spectre", Background = OxyColors.AliceBlue };

            labelPMData.Visibility = Visibility.Hidden;

        }

        private void initGraph(bool fixedXaxis)
        {
            MyModel.Series.Clear();


            if (fixedXaxis)
            {
                MyModel.Axes.Clear();
                MyModel.Axes.Add(item: new OxyPlot.Axes.LinearAxis { Position = AxisPosition.Bottom, Maximum = tempsAcquisition, Minimum = 0 });
            }
        }


        private static async Task RunPeriodicAsync(Action onTick,
                                          TimeSpan dueTime,
                                          TimeSpan interval,
                                          CancellationToken token)
        {
            // Initial wait time before we begin the periodic loop.
            if (dueTime > TimeSpan.Zero)
                await Task.Delay(dueTime, token);

            // Repeat this loop until cancelled.
            while (!token.IsCancellationRequested)
            {
                // Call our onTick function.
                onTick?.Invoke();

                // Wait to repeat again.
                if (interval > TimeSpan.Zero)
                    await Task.Delay(interval, token);
            }
        }
        private async Task InitializeAsync()
        {
            int t;
            // Int32.TryParse(Properties.Settings.Default["dataLogInterval"].ToString(), out t);
            var dueTime = TimeSpan.FromSeconds(0);
            var interval = TimeSpan.FromSeconds(1);

            // TODO: Add a CancellationTokenSource and supply the token here instead of None.
            await RunPeriodicAsync(requestPMData, dueTime, interval, ctsAcquisition.Token);
        }
        private async Task InitializeAsyncTic()
        {
            int t;
            // Int32.TryParse(Properties.Settings.Default["dataLogInterval"].ToString(), out t);
            var dueTime = TimeSpan.FromSeconds(0);
            var interval = TimeSpan.FromSeconds(1);

            // TODO: Add a CancellationTokenSource and supply the token here instead of None.
            await RunPeriodicAsync(cycle, dueTime, interval, ctsTic.Token);
        }

        public void getParams()
        {
            int result;
            if (!int.TryParse(textBoxPreRincage.Text, out result)) return;
            tempsPreRincage = result;
            if (!int.TryParse(textBoxPompage.Text, out result)) return;
            tempsPompage = result;
            if (!int.TryParse(textBoxRincage.Text, out result)) return;
            tempsRincage = result;
            if (!int.TryParse(textBoxAcquisition.Text, out result)) return;
            tempsAcquisition = result;
            if (!int.TryParse(textBoxNbEchantillons.Text, out result)) return;
            nbEchantillons = result;
        }

        private void initVannes()
        {

            if (initDAQ() >= 0)
            {
                labelVannesStatus.Content = "Connectées";
                daq.FlashLED();
                Vanne2Inject();
                Vanne1Home();
                btnHome.IsEnabled = true;
                btnStep.IsEnabled = true;
                btnInject.IsEnabled = true;
                vannesConnected = true;

                if (COMPMConnected) btnStart.IsEnabled = true;
            }            
        }
        public int initDAQ()
        {
                //MccDaq.DaqDeviceManager.IgnoreInstaCal();
                daq = new MccDaq.MccBoard(BoardNum);
                if (daq.BoardName.Length >0)
                {
                    if (daq.DConfigPort((DigitalPortType)10, DigitalPortDirection.DigitalOut).Value != ErrorInfo.ErrorCode.NoErrors) return -1;
                    if (daq.DConfigPort((DigitalPortType)11, DigitalPortDirection.DigitalOut).Value != ErrorInfo.ErrorCode.NoErrors) return -1;
                    if (daq.DConfigPort((DigitalPortType)12, DigitalPortDirection.DigitalOut).Value != ErrorInfo.ErrorCode.NoErrors) return -1;


                    RetVal = daq.DOut((DigitalPortType)10, (short)(0));
                    RetVal = daq.DOut((DigitalPortType)11, (short)(0));
                    RetVal = daq.DOut((DigitalPortType)12, (short)(0));

                    return BoardNum;
                }
            return -1;
        }

        private ErrorInfo Vanne1Step()
        {
            RetVal = daq.DOut((DigitalPortType)PORT, (short)(2));//pin 21 = 0, pin 22 = 1;
            Thread.Sleep(100);
            RetVal = daq.DOut((DigitalPortType)PORT, (short)(3));//pin 21 = 1, pin 22 = 1;
            Thread.Sleep(100);
            RetVal = daq.DOut((DigitalPortType)PORT, (short)(2));//pin 21 = 0, pin 22 = 1;

            Vanne1Pos++;
            if (Vanne1Pos >= 6)
            {
                labelVanne1Pos.Content = "Position: HOME";
                Vanne1Pos = 0;
            }
            else labelVanne1Pos.Content = "Position: " + (Vanne1Pos + 1).ToString(); 
        
            return RetVal;
        }

        private ErrorInfo Vanne1Home()
        {
            RetVal = daq.DOut((DigitalPortType)PORT, (short)(1));//pin 21 = 1, pin 22 = 0;
            Vanne1Pos = 0;
            labelVanne1Pos.Content = "Position: HOME";
            return RetVal;
        }

        private ErrorInfo Vanne2Load()
        {
            RetVal = daq.DOut((DigitalPortType)12, (short)(2));//pin 1 = 0, pin 2 = 1;
            labelVanne2Pos.Content = "Position: LOAD";
            btnInject.Content = "INJECT";
            Vanne2Pos = 0;
            return RetVal;
        }

        private ErrorInfo Vanne2Inject()
        {
            RetVal = daq.DOut((DigitalPortType)12, (short)(1));//pin 1 = 1, pin 2 = 0;
            labelVanne2Pos.Content = "Position: INJECT";
            btnInject.Content = "LOAD";
            Vanne2Pos = 1;
            return RetVal;
        }

        private void updatePortsList(ComboBox cb)
        {
            int index = -1;
            using (var searcher = new ManagementObjectSearcher("SELECT * FROM Win32_PnPEntity WHERE Caption like '%(COM%'"))
            {
                var portnames = SerialPort.GetPortNames();
                var ports = searcher.Get().Cast<ManagementBaseObject>().ToList().Select(p => p["Caption"].ToString());

                var portList = portnames.Select(n => n + " - " + ports.FirstOrDefault(s => s.Contains(n))).ToList();
                cb.Items.Clear();
                foreach (string s in portList)
                {
                    if (s.Contains("USB")) cb.Items.Add(s);
                    if (s.Contains("Prolific USB-to-Serial")) index = cb.Items.Count-1;
                }
                cb.Items.Add("Refresh List");
            }
            if (index >= 0) cb.SelectedIndex = index;
        }

        private void connectCOM(ComboBox cb)
        {
            if(cb == comboBoxPortsPM)
            {
                connectCOMPortPM();
            }
        }



        /*
         * PM DATA:
         * D: set high voltage ON
         * S: request a reading. Result (4 bytes) will be received one second later. if most significant bit of most significant byte =1, then data is an error
         * 
         * return value:
         * BC: bad command
         * BA: bad argument
         * VA: Valid
         * */

        private void connectCOMPortPM()
        {
            if (serialPM.IsOpen) serialPM.Close();
            if (!COMPMConnected)
            {
                if (comboBoxPortsPM.SelectedItem == null) MessageBox.Show("Please select a COM Port", "Error");
                else
                {
                    string name = comboBoxPortsPM.SelectedItem.ToString();
                    name = name.Substring(0, name.IndexOf(' '));
                    //Sets up serial port
                    serialPM.PortName = name;
                    serialPM.BaudRate = 9600;
                    serialPM.Handshake = System.IO.Ports.Handshake.None;
                    serialPM.Parity = Parity.None;
                    serialPM.DataBits = 8;
                    serialPM.StopBits = StopBits.One;
                    serialPM.ReadTimeout = 200;
                    serialPM.WriteTimeout = 50;
                    serialPM.Open();

                    if (serialPM.IsOpen)
                    {
                        serialPM.DataReceived += new System.IO.Ports.SerialDataReceivedEventHandler(ReceivePM);
                        PMcommand = "D";
                        Send_Data(serialPM);
                    }
                }


            }
            else
            {
                try // just in case serial port is not open could also be acheved using if(serial.IsOpen)
                {
                    serialPM.Close();
                    if (!serialPM.IsOpen)
                    {
                        COMPMConnected = false;

                        btnStartAcquisition.IsEnabled = false;
                        if (vannesConnected) btnStart.IsEnabled = false;
                    }
                }
                catch
                {
                }
            }
        }

        private void Send_Data(SerialPort serial)
        {
            SerialCmdSend(PMcommand + "\r",serial);
        }
        public void SerialCmdSend(string data, SerialPort serial)
        {
            if (serial.IsOpen)
            {
                try
                {
                    // Send the binary data out the port
                    byte[] hexstring = Encoding.UTF8.GetBytes(data);
                    //There is a intermitant problem that I came across
                    //If I write more than one byte in succesion without a 
                    //delay the PIC i'm communicating with will Crash
                    //I expect this id due to PC timing issues ad they are
                    //not directley connected to the COM port the solution
                    //Is a ver small 1 millisecound delay between chracters
                    foreach (byte hexval in hexstring)
                    {
                        byte[] _hexval = new byte[] { hexval }; // need to convert byte to byte[] to write
                        serial.Write(_hexval, 0, 1);
                        //Thread.Sleep(1);
                    }
                }
                catch (Exception ex)
                {
                }
            }
            else
            {
            }
        }

        void requestPMData()
        {
            PMcommand = "S";
            SerialCmdSend(PMcommand + "\r", serialPM);

        }


        private delegate void UpdateUiTextDelegate(string text);
        private void ReceivePM(object sender, System.IO.Ports.SerialDataReceivedEventArgs e)
        {
            // Collecting the characters received to our 'buffer' (string).
            received_data = serialPM.ReadExisting();
            Dispatcher.Invoke(DispatcherPriority.Send, new UpdateUiTextDelegate(DisplayData), received_data);
        }
        private void DisplayData(string text)
        {
            statusLabel2.Text = "";
            float result = -999;
            try
            {
                byte[] str = Encoding.UTF8.GetBytes(text);
                    foreach(byte b in str)
                    {
                        if(byteCount<4) hexstring[byteCount] = b;
                        byteCount++;
                    }
                

                if (byteCount >= 4 && acquisitionEnCours)
                {
                    byte b3 = hexstring[0];
                    byte b2 = hexstring[1];
                    byte b1 = hexstring[2];
                    byte b0 = hexstring[3];
                    if (b3 < 128)
                    {
                        if (b3 <= 0) b3 = 0;
                        result = 16777216 * b3 + 65536 * b2 + 256 * b1 + b0;
                        storeNewDataPoint(result);
                        dpCounter++;
                    }
                    else
                    {
                        //Overflow
                    }
                    byteCount = 0;
                }
                else
                {
                    PM_ACK += text;
                    COMPMConnected = true;
                    if (PM_ACK == "VA")
                    {
                        byteCount = 0;
                        if (PMcommand.Contains("D"))
                        {
                            PM_ACK = "";
                            COMPMConnected = true;

                            btnStartAcquisition.IsEnabled = true;
                            if (vannesConnected) btnStart.IsEnabled = true;
                            labelPMStatus.Content = "Connecté";
                            //TODO: afficher haute tension ON
                        }
                        else if (PMcommand.Contains("S"))
                        {
                            //TODO: receive data
                        }
                        else if (PMcommand.Contains("V00"))
                        {
                            //TODO: afficher haute tension OFF
                            COMPMConnected = false;

                            btnStartAcquisition.IsEnabled = false;
                            if (vannesConnected) btnStart.IsEnabled = false;
                            labelPMStatus.Content = "OFF";
                        }
                    }
                    else
                    {
                        if (PM_ACK.Contains ("BC") || PM_ACK.Contains("BA"))
                        {
                            PM_ACK = "";
                            statusLabel2.Text = "Probleme de communication avec le PM";
                        }
                    }
                }
            }
            catch (Exception ex)
            {
            }
            
        }

        private void startPMAcquisition(bool inCycle)
        {

            addPlot();
            if (inCycle)
            {
                int nbSeries = dataSeries.Count();

                var ds = new DataSerie(nbSeries, textBoxNomEchantillon.Text);
                ds.heureDebut = DateTime.Now;
                dataSeries.Add(ds);
            }
            ctsAcquisition = new CancellationTokenSource();
            InitializeAsync();
            labelPMData.Visibility = Visibility.Visible;

            acquisitionEnCours = true;
        }

        private void addPlot()
        {
            OxyColor color = new OxyColor();
            color = OxyColors.Black;

            switch (numeroEchantillon)
            {
                case 1:
                    color = OxyColors.Black;
                    break;
                case 2:
                    color = OxyColors.Red;
                    break;
                case 3:
                    color = OxyColors.Green;
                    break;
                case 4:
                    color = OxyColors.Blue;
                    break;
                default:
                    color = OxyColors.Purple;
                    break;
            }

            var series1 = new LineSeries { Title = "Echantillon " + numeroEchantillon.ToString(), MarkerType = MarkerType.Circle, Color = color };
            MyModel.Series.Add(series1);
        }

        private void stopPMAcquisition()
        {
            ctsAcquisition.Cancel();
            labelPMData.Visibility = Visibility.Hidden;
            acquisitionEnCours = false;
            dpCounter = 0;
        }

        private void storeNewDataPoint(float value)
        {
            DataPoint dp = new DataPoint(dpCounter, value);
            ((LineSeries)MyModel.Series[numeroEchantillon]).Points.Add(dp); ;
            
            MyModel.InvalidatePlot(true);

            try
            {

                dataSeries.Last().dps.Add(dp);
            }
            catch(Exception ex)
            {

            }
            labelPMData.Content = String.Format("Valeur PM:{0:0}", dp.Y.ToString());
        }


        private void comboBoxPorts_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            ComboBox cb = (ComboBox)sender;
            if (cb.SelectedItem != null)
            {

                if (cb.SelectedItem.ToString().Contains("Refresh"))
                {
                    updatePortsList(cb);
                }
                else
                {
                    connectCOM(cb);
                }
            }
        }

        private void cycle()
        {
            /*
             * STep 1: V1 = HOME et V2 = Inject
             * Step2: demarrage: V1 = 2 et V2 = Load
             * delay temps de pompage
             * Step3: V1 = 3 et V2 = Load
             * delay 5s jusqua V1=6
             * STep 4:V1=1 et V2=load
             * delay temps de rincage - 20s
             * Step 5: V1=1 et V2 = inject + acquisition pendant temps acquisition
             * 
             * repeat x nb echantillons
             */
            Label label = labelNbEchantillons;
            switch (CycleEnCours)
            {
                case cycleEnCours.IDLE:

                    dpCounter = 0;
                    if (numeroEchantillon == nbEchantillons)
                    {
                        ctsTic.Cancel();
                        
                        cycleRunning = false;
                        updateTexts();
                    }
                    if (cycleStepChange)
                    {
                        
                        cycleStepChange = false;
                        Vanne1Home();
                        Vanne2Load();
                    }
                    break;
                case cycleEnCours.PRERINCAGE:
                    label = labelPreRincage;
                    if (cycleStepChange)
                    {
                        timerEnCours = tempsPreRincage;
                        cycleStepChange = false;
                        Vanne1Home();
                        Vanne2Load();
                    }
                    textBoxSet(timerEnCours, true, textBoxPreRincage);
                    break;
                case cycleEnCours.POMPAGE:
                    label = labelPompage;
                    if (cycleStepChange)
                    {
                        timerEnCours = tempsPompage;
                        cycleStepChange = false;
                        Vanne1Step();
                        Vanne2Load();
                        textBoxSet(tempsPreRincage, false, textBoxPreRincage);
                    }
                    textBoxSet(timerEnCours, true, textBoxPompage);
                    break;
                case cycleEnCours.RINCAGE:
                    label = labelRincage;
                    timerRincage++;
                    if (cycleStepChange)
                    {
                        timerEnCours = tempsRincage;
                        cycleStepChange = false;
                        Vanne1Step();
                        Vanne2Load();
                        timerRincage = 0;
                        textBoxSet(tempsPompage, false, textBoxPompage);
                    }
                    else if (timerRincage == 5 && Vanne1Pos != 0)
                    {
                        Vanne1Step();
                        timerRincage = 0;
                    }
                    textBoxSet(timerEnCours, true, textBoxRincage);



                    break;
                case cycleEnCours.ACQUISITION:
                    label = labelAcquisition;
                    if (cycleStepChange)
                    {

                        timerEnCours = tempsAcquisition;
                        cycleStepChange = false;
                        Vanne1Home();
                        Vanne2Inject();
                        startPMAcquisition(true);
                        textBoxSet(tempsRincage, false, textBoxRincage);
                    }
                    textBoxSet(timerEnCours, true, textBoxAcquisition);
                    break;
                case cycleEnCours.FINITION:
                    label = labelAcquisition;
                    if (cycleStepChange)
                    {
                        timerEnCours = 2;
                        cycleStepChange = false;

                        stopPMAcquisition();
                        textBoxSet(tempsAcquisition, false, textBoxAcquisition);
                    }
                    break;

            }
            updateLabelenCours(label);
            timerEnCours--;
            if (timerEnCours <= 0) {
                cycleStepChange = true;
                if (CycleEnCours == cycleEnCours.FINITION)
                {
                    CycleEnCours = cycleEnCours.IDLE;
                    dataSeries.Last().process();

                    ICollectionView dataView =
              CollectionViewSource.GetDefaultView(Journal.ItemsSource);
                    dataView.Refresh();
                    numeroEchantillon++;
                }
                else CycleEnCours++;
            }
        }

        private void textBoxSet(int value, bool enCours, TextBox tb)
        {

            tb.Text = value.ToString();
            if (enCours)
            {
                tb.FontStyle = FontStyles.Italic;
                tb.Foreground = Brushes.Red;
            }
            else
            {
                tb.FontStyle = FontStyles.Normal;
                tb.Foreground = Brushes.Black;
            }
        }

        private void updateLabelenCours(Label label)
        {
            labelPreRincage.Foreground = Brushes.Black;
            labelPreRincage.FontStyle = FontStyles.Normal;
            labelPreRincage.FontWeight = FontWeights.Normal;
            labelRincage.Foreground = Brushes.Black;
            labelRincage.FontStyle = FontStyles.Normal;
            labelRincage.FontWeight = FontWeights.Normal;
            labelPompage.Foreground = Brushes.Black;
            labelPompage.FontStyle = FontStyles.Normal;
            labelPompage.FontWeight = FontWeights.Normal;
            labelAcquisition.Foreground = Brushes.Black;
            labelAcquisition.FontStyle = FontStyles.Normal;
            labelAcquisition.FontWeight = FontWeights.Normal;

            if (label != labelNbEchantillons)
            {
                label.Foreground = Brushes.Red;
                label.FontStyle = FontStyles.Italic;
                label.FontWeight = FontWeights.Bold;
            }



        }

        private void Exit_Click(object sender, RoutedEventArgs e)
        {

        }

        private void SaveData_Click(object sender, RoutedEventArgs e)
        {
            SaveFileDialog saveFileDialog = new SaveFileDialog();
            saveFileDialog.Filter = "Csv file (*.csv)|*.csv";
            if (saveFileDialog.ShowDialog() == true)
            {
                saveToFile(saveFileDialog.FileName);
            }
        }

        private void btnStart_Click(object sender, RoutedEventArgs e)
        {
            
            cycleRunning = !cycleRunning;
            updateTexts();


            if (cycleRunning)
            {
                startCycle();
            }
            else
            {
                stopCycle();
            }
            
        }
        private void stopCycle()
        {
            ctsTic.Cancel();

            if (acquisitionEnCours) stopPMAcquisition();

            Vanne1Home();
            Vanne2Inject();

            textBoxSet(tempsPreRincage, false, textBoxPreRincage);
            textBoxSet(tempsRincage, false, textBoxRincage);
            textBoxSet(tempsPompage, false, textBoxPompage);
            textBoxSet(tempsAcquisition, false, textBoxAcquisition);
            updateLabelenCours(labelNbEchantillons);
        }
        void updateTexts()
        {
            textBoxAcquisition.IsEnabled = !cycleRunning;
            textBoxNbEchantillons.IsEnabled = !cycleRunning;
            textBoxPompage.IsEnabled = !cycleRunning;
            textBoxPreRincage.IsEnabled = !cycleRunning;
            textBoxRincage.IsEnabled = !cycleRunning;

            btnStep.IsEnabled = !cycleRunning;
            btnHome.IsEnabled = !cycleRunning;
            btnInject.IsEnabled = !cycleRunning;
            btnStartAcquisition.IsEnabled = !cycleRunning && COMPMConnected;
            btnClearJournal.IsEnabled =
                !cycleRunning;
            btnSaveJournal.IsEnabled =
                !cycleRunning;

            btnStart.Content = cycleRunning ? "STOP" : "START";
        }


        void startCycle()
        {
            MyModel.InvalidatePlot(true);
            numeroEchantillon = 0;
            if (acquisitionEnCours) stopPMAcquisition();
            getParams();
            Vanne1Home();
            Vanne2Load();

            timerEnCours = tempsPreRincage;
            CycleEnCours = cycleEnCours.PRERINCAGE;


            initGraph(true);
            //dataSeries.Clear();
            
            ctsTic = new CancellationTokenSource();
            InitializeAsyncTic();

            
        }

        private void btnInject_Click(object sender, RoutedEventArgs e)
        {
            if (Vanne2Pos == 0) {
                Vanne2Inject();
                btnInject.Content = "LOAD";
                labelVanne2Pos.Content = "Position: INJECT";
            }
            else
            {
                Vanne2Load();
                btnInject.Content = "INJECT";
                labelVanne2Pos.Content = "Position: LOAD";
            }
        }

        private void btnHome_Click(object sender, RoutedEventArgs e)
        {
            Vanne1Home();
            
        }

        private void btnStep_Click(object sender, RoutedEventArgs e)
        {
            Vanne1Step();
        }

        private void lvColumnHeader_Click(object sender, RoutedEventArgs e)
        {
            GridViewColumnHeader headerClicked = e.OriginalSource as GridViewColumnHeader;
            ListSortDirection direction;

            if (headerClicked != null)
            {
                if (headerClicked.Role != GridViewColumnHeaderRole.Padding)
                {
                    if (headerClicked != _lastHeaderClicked)
                    {
                        direction = ListSortDirection.Ascending;
                    }
                    else
                    {
                        if (_lastDirection == ListSortDirection.Ascending)
                        {
                            direction = ListSortDirection.Descending;
                        }
                        else
                        {
                            direction = ListSortDirection.Ascending;
                        }
                    }

                    string header = headerClicked.Tag as string;
                    Sort(header, direction);

                    _lastHeaderClicked = headerClicked;
                    _lastDirection = direction;
                }
            }
        }

        public void Sort(string sortBy, ListSortDirection direction)
        {
            ICollectionView dataView =
              CollectionViewSource.GetDefaultView(Journal.ItemsSource);

            dataView.SortDescriptions.Clear();
            SortDescription sd = new SortDescription(sortBy, direction);
            dataView.SortDescriptions.Add(sd);
            dataView.Refresh();
        }

        private void ConVannes_Click(object sender, RoutedEventArgs e)
        {
            initVannes();
        }

        private void btnStartAcquisition_Click(object sender, RoutedEventArgs e)
        {
            if (acquisitionEnCours)
            {
                stopPMAcquisition();
                btnStartAcquisition.Content = "START ACQUISITION";
            }
            else
            {
                initGraph(false);
                startPMAcquisition(false); 
                btnStartAcquisition.Content = "STOP ACQUISITION";
            }
        }

        private void saveToFile(string filePath)
        {
            if (!System.IO.File.Exists(filePath))
            {
                //Write headers
                String header = "Numero;Nom;Moyenne Debut;Moyenne Fin;Min;Max;Integrale;Heure Debut;Heure Fin;Nb points";

               
                header += "\n";
                System.IO.File.WriteAllText(filePath, header);
            }

            foreach (DataSerie ech in dataSeries)
            {
                string data = String.Format("{0:0};{6};{1:0.00};{2:0.00};{3:0};{4:0};{5:0.00};{7};{8};{9:0};\n", ech.number, ech.MoyBasse, ech.MoyHaute, ech.Min, ech.Max, ech.Integrale, ech.Nom, ech.heureDebut.ToString("yyyy/MM/dd hh:mm:ss"), ech.heureFin.ToString("yyyy/MM/dd hh:mm:ss"), ech.NbPoints) ;
                System.IO.File.AppendAllText(filePath, data);
            }

        }

        private void btnClearJournal_Click(object sender, RoutedEventArgs e)
        {
            string sMessageBoxText = "Etes vous sûr.e.s de vouloir effacer toutes les données?";
            string sCaption = "Effacer les données";

            MessageBoxButton btnMessageBox = MessageBoxButton.YesNoCancel;
            MessageBoxImage icnMessageBox = MessageBoxImage.Warning;

            MessageBoxResult rsltMessageBox = MessageBox.Show(sMessageBoxText, sCaption, btnMessageBox, icnMessageBox);

            switch (rsltMessageBox)
            {
                case MessageBoxResult.Yes:
                    dataSeries.Clear();
                    numeroEchantillon = 0;
                    break;

                default:
                    /* ... */
                    break;
            }
        }
    }
}
