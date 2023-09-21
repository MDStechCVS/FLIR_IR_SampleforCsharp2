using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data;
using System.Drawing;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;

using System.Diagnostics;

using PvDotNet;
using PvGUIDotNet;
using System.Threading;

namespace ThermalTest2
{
    public partial class Form1 : Form
    {
        // FLIR A6xx
        const int mCamWidth = 640;
        const int mCamHeight = 480;
        const float mConvertOffsetVal = 0.01f;  // A6xx 10mK
        //const float mConvertOffsetVal = 0.1f;   // A6xx 100mK

        // FLIR Ax5
        //const int mCamWidth = 320;
        //const int mCamHeight = 256;
        ////const float mConvertOffsetVal = 0.04f;  // A35 10mK
        //const float mConvertOffsetVal = 0.4f;   // A35 100mK


        public Form1()
        {
            InitializeComponent();
 
            PictureBoxIpl.SizeMode = PictureBoxSizeMode.StretchImage;

            maxSpot = new MeasureSpotValue(Color.Red);
            minSpot = new MeasureSpotValue(Color.Blue);

            // 측정 영역 박스 좌표, 크기 설정
            roiBox = new MeasureBoxValue(Color.Black, 100, 100, 100, 100);

            bmp = new Bitmap(mCamWidth, mCamHeight);
        }
        
        /// <summary>
        /// Connect, configure and control a GigE Vision device
        /// </summary>
        private PvDevice mDevice = null;
        /// <summary>
        /// Receive data from a GigE Vision transmitter 
        /// (data receiver properties dynamically accessible using a PvGenParameterArray interface). 
        /// </summary>
        private PvStream mStream = null;
        /// <summary>
        /// Helper class for receiving data from a GigE Vision transmitter
        /// </summary>
        private PvPipeline mPipeline = null;

        private const UInt16 cBufferCount = 16;

        private Thread mThread = null;

        /// <summary>
        /// Is in Image proccessing
        /// </summary>
        private bool bProcessing = false;


        static int stIntCamFrameArray = mCamWidth * mCamHeight;//81920;

        // max min icon
        MeasureSpotValue maxSpot;
        MeasureSpotValue minSpot;
        MeasureBoxValue roiBox;

        private void Step6Disconnecting()
        {
            if (mStream != null)
            {
                // Close and release stream
                mStream.Close();
                mStream = null;
            }

            if (mDevice != null)
            {
                // Disconnect and release device
                mDevice.Disconnect();
                mDevice = null;
            }
        }

        /// <summary>
        /// Pleora Device 의 기본값 설정
        /// </summary>
        private void InitPleoraDevice()
        {
            PvGenString lManufacturerInfo = (PvGenString)(mDevice.Parameters.Get("DeviceManufacturerInfo"));
            PvGenEnum lPixelFormat = (PvGenEnum)(mDevice.Parameters.Get("PixelFormat"));
            PvGenEnum lTLUTMode = (PvGenEnum)(mDevice.Parameters.Get("TemperatureLinearModeReg"));
            if (lTLUTMode == null)
            {
                lTLUTMode = (PvGenEnum)(mDevice.Parameters.Get("TemperatureLinearMode"));
            }

            if (lTLUTMode == null)
            {
                //mDevice.GenParameters.SetEnumValue("IRFormat", 0);  /* IRFormat을 Radiometric으로 설정 */
            }

            string lInfoStr = "N/A";
            lInfoStr = lManufacturerInfo.Value;

            string devInfo = lInfoStr;
            System.Diagnostics.Debug.WriteLine("devInfo : " + lInfoStr);

            if (devInfo.Contains("A645"))   // FLIR Axx
            {
                PvGenEnum lRFormat1 = (PvGenEnum)(mDevice.Parameters.Get("IRFormat"));
                lRFormat1.ValueInt = 2;     // TemperatureLinear 10mk
            }
            else if (devInfo.Contains("ATAU"))  // FLIR Ax5
            {
                lPixelFormat.ValueInt = 0x01100025; /* PIXEL_FORMAT_MONO_14 */

                PvGenEnum lDigitalOutput = (PvGenEnum)(mDevice.Parameters.Get("DigitalOutput"));
                PvGenEnum lCMOSBitDepth = (PvGenEnum)(mDevice.Parameters.Get("CMOSBitDepth"));

                lDigitalOutput.ValueInt = 3;

                if (lCMOSBitDepth != null && lCMOSBitDepth.ValueInt != 0)
                    lCMOSBitDepth.ValueInt = 0; // 14 bit
                try
                {
                    if (lTLUTMode != null)
                    {
                        lTLUTMode.ValueInt = 1; // Turn it on
                    }
                }
                catch (System.Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine(ex.Message);
                }

            }

        }

        /// <summary>
        /// Pleora 연결 후 필요 설정
        /// </summary>
        private void Step2Configuring()
        {
            try
            {
                // Perform GigE Vision only configuration
                PvDeviceGEV lDGEV = mDevice as PvDeviceGEV;
                if (lDGEV != null)
                {
                    // Negotiate packet size
                    lDGEV.NegotiatePacketSize();

                    // Set stream destination.
                    PvStreamGEV lSGEV = mStream as PvStreamGEV;
                    lDGEV.SetStreamDestination(lSGEV.LocalIPAddress, lSGEV.LocalPort);
                }

                // Read payload size, preallocate buffers of the pipeline.
                Int64 lPayloadSize = mDevice.PayloadSize;

                // Get minimum buffer count, creates and allocates buffer.
                UInt32 lBufferCount = (mStream.QueuedBufferMaximum < cBufferCount) ? mStream.QueuedBufferMaximum : cBufferCount;
                PvBuffer[] lBuffers = new PvBuffer[lBufferCount];
                for (UInt32 i = 0; i < lBufferCount; i++)
                {
                    lBuffers[i] = new PvBuffer();
                    lBuffers[i].Alloc((UInt32)lPayloadSize);
                }

                // Queue all buffers in the stream.
                for (UInt32 i = 0; i < lBufferCount; i++)
                {
                    mStream.QueueBuffer(lBuffers[i]);
                }
            }
            catch (PvException ex)
            {
                Step6Disconnecting();
                MessageBox.Show(ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                Close();
            }
        }

        // Point index에서 X, Y, 좌표를 알아낸다.
        private void getXY(int sourceIndex, int sourceWidth, out int x, out int y)
        {
            y = sourceIndex / sourceWidth;
            x = sourceIndex % sourceWidth;
        }

        private Bitmap bmp = null;

        int step = 256 / 4; // 총 4개의 간격으로 나눔

        /// <summary>
        /// MdsSdkControl 데이터 Receiver delegate function
        /// </summary>
        /// <param name="data">수신 데이터</param>
        /// <param name="w">width</param>
        /// <param name="h">height</param>
        /// <param name="minval">minimum value</param>
        /// <param name="maxval">maximum value</param>
        delegate void DelegateCtrlData_Receiver(UInt16[] data, int w, int h, ushort minval, ushort maxval);
        /// <summary>
        /// MdsSdkControl 데이터 Receiver
        /// 열화상 카메라로 부터 받은 데이터로 화면을 구성
        /// </summary>
        /// <param name="data">수신 데이터</param>
        /// <param name="w">width</param>
        /// <param name="h">height</param>
        /// <param name="minval">minimum value</param>
        /// <param name="maxval">maximum value</param>
        void CtrlData_Receiver(UInt16[] data, int w, int h, ushort minval, ushort maxval)
        {
            if (data == null)
                return;

            lock (this)
            {
                //SetImage를 수행중이면 리턴.(화면 갱신 skip)
                if (bProcessing)
                {
                    return;
                }
            }

            // UI thread 사용 여부 확인
            if (this.InvokeRequired)
            {
                this.Invoke(new DelegateCtrlData_Receiver(CtrlData_Receiver), new object[] { data, w, h, minval, maxval });
                return;
            }

            try
            {
                lock (this)
                    bProcessing = true;


                Color col;

                //x 는 image의 width
                //y 는 image의 hediht
                int x, y;

                // Box 내 영역의 최대 최소 온도값 초기화
                if (roiBox.GetIsVisible())
                {
                    roiBox.ResetMinMax();
                }

                // Black and white
                //for (int a = 0; a < data.Length; a++)
                //{
                //    //for문을 돌며 이미지 그레이스케일화
                //    getXY(a, mCamWidth, out x, out y);

                //    int rVal = (int)((data[a] - minval) * 255 / (maxval - minval));
  
                //    col = Color.FromArgb(rVal, rVal, rVal);
                //    bmp.SetPixel(x, y, col);

                //    // Box 내 영역의 최대 최소 온도값 체크
                //    if (roiBox.GetIsVisible())
                //    {
                //        roiBox.CheckXYinBox(x, y, data[a]);
                //    }
                //}

                // Rainbow colors
                for (int a = 0; a < data.Length; a++)
                {
                    getXY(a, mCamWidth, out x, out y);

                    int rVal = (int)((data[a] - minval) * 255 / (maxval - minval));

                    if (rVal < step) //Blue to Cyan
                    {
                        col = Color.FromArgb(0, rVal * 4, 255);
                    }
                    else if (rVal < step * 2) //Cyan to Green
                    {
                        col = Color.FromArgb(0, 255, 255 - (rVal - step) * 4);
                    }
                    else if (rVal < step * 3) //Green to Yellow
                    {
                        col = Color.FromArgb((rVal - step * 2) * 4, 255, 0);
                    }
                    else //Yellow to Red
                    {
                        col = Color.FromArgb(255, 255 - (rVal - step * 3) * 4, 0);
                    }
                    
                    bmp.SetPixel(x, y, col);

                    // Box 내 영역의 최대 최소 온도값 체크
                    if (roiBox.GetIsVisible())
                    {
                        roiBox.CheckXYinBox(x, y, data[a]);
                    }
                }

                Graphics gr = Graphics.FromImage(bmp);

                int maxX = 0;
                int maxY = 0;
                int minX = 0;
                int minY = 0;

                // max spot get x, y;
                getXY(maxSpot.GetPointIndex(), mCamWidth, out maxX, out maxY);
                getXY(minSpot.GetPointIndex(), mCamWidth, out minX, out minY);

                maxSpot.SetXY(gr, maxX, maxY);
                minSpot.SetXY(gr, minY, minY);

                double minValue = (((float)(minval) * mConvertOffsetVal) - 273.15f);
                double maxValue = (((float)(maxval) * mConvertOffsetVal) - 273.15f);

                label1.Text = (string.Format("Max: {1}°C, Min: {0}°C", minValue.ToString("0.0"), maxValue.ToString("0.0")));

                // ROI Box
                if (roiBox.GetIsVisible())
                {
                    roiBox.SetXYWH(gr);
                    roiBox.SetMax(gr);
                    roiBox.SetMin(gr);

                    ushort usMin = 0;
                    ushort usMax = 0;

                    roiBox.GetMinMax(out usMin, out usMax);

                    double minBox = (((float)(usMin) * mConvertOffsetVal) - 273.15f);
                    double maxBox = (((float)(usMax) * mConvertOffsetVal) - 273.15f);

                    label2.Text = (string.Format("Max: {1}°C, Min: {0}°C", minBox.ToString("0.0"), maxBox.ToString("0.0")));
                }


                PictureBoxIpl.Image = bmp;
                //bmp.Save("c:\\test.jpeg", System.Drawing.Imaging.ImageFormat.Jpeg);

                
            }
            catch (Exception e)
            {
                Debug.WriteLine("[CtrlEvent] " + e.ToString());
            }

            lock (this)
                bProcessing = false;

            return;

        }

        private void ThreadProc(object aParameters)
        {
            object[] lParameters = (object[])aParameters;
            Form1 lThis = (Form1)lParameters[0];

            UInt16[] pixArr = new UInt16[stIntCamFrameArray];

            for (;;)
            {
                PvBuffer lBuffer = null;
                PvResult lOperationResult = new PvResult(PvResultCode.OK);

                // Retrieve next buffer from acquisition pipeline
                PvResult lResult = lThis.mStream.RetrieveBuffer(ref lBuffer, ref lOperationResult, 100);
                if (lResult.IsOK)
                {
                    // Operation result of buffer is OK, display.
                    if (lOperationResult.IsOK)
                    {
                        ushort max16 = 0;
                        ushort min16 = 65535;

                        unsafe
                        {
                            if (lResult.IsOK)
                            {
                                
                                IntPtr ptr = (IntPtr)lBuffer.DataPointer;
              
                                byte* byteArr = (byte*)ptr.ToPointer();
                                
                                for (int i = 0; i < stIntCamFrameArray; i++)
                                {
                                    pixArr[i] = (ushort)(byteArr[i * 2] | (byteArr[i * 2 + 1] << 8));
                                    //pixArr[i] = (ushort)(byteArr[i]);

                                    ushort sample = pixArr[i];

                                    // 전체 화면에서 최대 최소 구하기
                                    if (min16 >= sample)
                                    {
                                        min16 = sample;
                                        minSpot.SetPointIndex(i);
                                        minSpot.SetTempVal(sample);
                                    }
                                    else if (max16 < sample)
                                    {
                                        max16 = sample;
                                        maxSpot.SetPointIndex(i);
                                        maxSpot.SetTempVal(sample);
                                    }
                                }
                                
                                // 영상, 최대 최소, 영역 그려주기
                                CtrlData_Receiver(pixArr, mCamWidth, mCamHeight, min16, max16);

                                // We got a buffer (good or not) we must release it back
                                mPipeline.ReleaseBuffer(lBuffer);
                            }
                        }
                    }

                    // We have an image - do some processing (...) and VERY IMPORTANT,
                    // re-queue the buffer in the stream object.
                    lThis.mStream.QueueBuffer(lBuffer);
                }

                Thread.Sleep(10);
            }
        }

        private void PleoraStartingStream()
        {
            // Start display thread.
            mThread = new Thread(new ParameterizedThreadStart(ThreadProc));
            Form1 lP1 = this;
            object[] lParameters = new object[] { lP1 };

            mThread.Start(lParameters);

            // Enables streaming before sending the AcquisitionStart command.
            mDevice.StreamEnable();

            // Start acquisition on the device
            mDevice.Parameters.ExecuteCommand("AcquisitionStart");
        }

        private void button1_Click(object sender, EventArgs e)
        {
            // Pop device finder, let user select a device.
            PvDeviceFinderForm lForm = new PvDeviceFinderForm();
            if ((lForm.ShowDialog() == DialogResult.OK) && (lForm.Selected != null))
            {
                try
                {
                    PvDeviceInfo lDeviceInfo = lForm.Selected;

                    // Connect device.
                    mDevice = PvDevice.CreateAndConnect(lDeviceInfo);

                    // Open stream.
                    mStream = PvStream.CreateAndOpen(lDeviceInfo.ConnectionID);
                    if (mStream == null)
                    {
                        MessageBox.Show("Unable to open stream.", Text);
                        return;
                    }

                    if (mDevice.IsConnected && (mStream.IsOpen))
                    {
                        InitPleoraDevice();

                        // Create pipeline
                        if (mPipeline == null)
                            mPipeline = new PvPipeline(mStream);

                        Step2Configuring();

                        PleoraStartingStream();
                    }

                }
                catch (PvException ex)
                {
                    Step6Disconnecting();
                    MessageBox.Show(ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Error);
                    Close();
                }
            }
            else
            {
                Close();
            }
                        
        }

        private void button2_Click(object sender, EventArgs e)
        {
            roiBox.SetIsVisible(true);
            button2.IsAccessible = false;
        }

        private void label1_Click(object sender, EventArgs e)
        {
            
        }

        private void Form1_FormClosed(object sender, FormClosedEventArgs e)
        {
            if (mThread != null)
            {
                mThread.Abort();
            }
        }
    }

    // 측정 영역 Box
    public class MeasureBoxValue
    {
        int mX;
        int mY;
        int mWidth;
        int mHeight;
        int mPointIdx;
        ushort mTempValue;
        bool mIsVisible = false;

        // Box 영역 내의 최대 최소 위치
        int mMax_X;
        int mMax_Y;
        int mMin_X;
        int mMin_Y;

        // Box 영역 내의 최대, 최소 온도값
        ushort mMax = 0;
        ushort mMin = 65535;


        Pen mPen = new Pen(Color.AliceBlue);
        Pen mPenMax = new Pen(Color.Red);
        Pen mPenMin = new Pen(Color.Blue);


        public MeasureBoxValue(Color cl, int nX, int nY, int nWidth, int nHeight)
        {
            mPen.Color = cl;

            mX = nX;
            mY = nY;
            mWidth = nWidth;
            mHeight = nHeight;
        }

        public void ResetMinMax()
        {
            mMax_X = 0;
            mMax_Y = 0;
            mMin_X = 0;
            mMin_Y = 0;
                        
            mMax = 0;
            mMin = 65535;
        }

        public void SetXYWH(Graphics gr)
        {
            gr.DrawRectangle(mPen, mX, mY, mWidth, mHeight);  // Box
        }

        public void SetMax(Graphics gr)
        {
            gr.DrawRectangle(mPenMax, mMax_X - 3, mMax_Y - 3, 6, 6);  // Box Max
        }

        public void SetMin(Graphics gr)
        {
            gr.DrawRectangle(mPenMin, mMin_X - 3, mMin_Y - 3, 6, 6);  // Box Min
        }

        public void GetMinMax(out ushort usMin, out ushort usMax)
        {
            usMin = mMin;
            usMax = mMax;
        }

        public void SetPointIndex(int nIndex)
        {
            mPointIdx = nIndex;
        }

        public int GetPointIndex()
        {
            return mPointIdx;
        }

        public void SetTempVal(ushort usTempValue)
        {
            mTempValue = usTempValue;
        }

        public bool GetIsVisible()
        {
            return mIsVisible;
        }

        public void SetIsVisible(bool bVal)
        {
            mIsVisible = bVal;
        }

        public bool CheckXYinBox(int nX, int nY, ushort tempVal)
        {
            bool rValue = false;

            if ( (mX <= nX) && ((mX + mWidth) >= nX))   // X 좌표가 범위 내에 있는지
            {
                if ((mY <= nY) && ((mY + mHeight) >= nY))   // Y 좌표가 범위 내에 있는지
                {
                    rValue = true;

                    // 최대 최소 온도 체크 후 백업
                    if (mMin >= tempVal)
                    {
                        mMin = tempVal;
                        mMin_X = nX;
                        mMin_Y = nY;
                    }
                    else if (mMax < tempVal)
                    {
                        mMax = tempVal;
                        mMax_X = nX;
                        mMax_Y = nY;
                    }
                }
            }
            return rValue;
        }
    }

    // 최대 최소 위치 표시
    public class MeasureSpotValue
    {
        int mPointIdx;
        ushort mTempValue;

        Pen mPen = new Pen(Color.AliceBlue);

        
        public MeasureSpotValue(Color cl)
        {
            mPen.Color = cl;
        }

        public void SetXY(Graphics gr, int nX, int nY)
        {
            gr.DrawLine(mPen, nX - 10,  nY,         nX + 10,    nY      );  // 수평
            gr.DrawLine(mPen, nX,       nY - 10,    nX,         nY + 10  );  // 수직
        }

        public void SetPointIndex(int nIndex)
        {
            mPointIdx = nIndex;
        }

        public int GetPointIndex()
        {
            return mPointIdx;
        }

        public void SetTempVal(ushort usTempValue)
        {
            mTempValue = usTempValue;
        }
    }
}
