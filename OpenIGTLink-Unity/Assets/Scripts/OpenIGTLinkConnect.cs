using UnityEngine;
using System;
using System.Net;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Collections;
using System.Threading;
using System.Collections.Generic;

#if !UNITY_EDITOR
using System.Threading.Tasks;
using Windows.Networking.Sockets;
#endif

//using System.Runtime.InteropServices;


public class OpenIGTLinkUnityConnect : MonoBehaviour
{

    public int scaleMultiplier = 1000; // Metres to millimetres

    //Set from config.txt, which is located in the project folder when run from the editor
    public string ipString = "127.0.0.1";
    public int port = 18944;
    public GameObject[] GameObjects;
    public int msDelay = 33;

    private float totalTime = 0f;

    //CRC ECMA-182
    private CRC64 crcGenerator;
    private string CRC;
    private string crcPolynomialBinary = "10100001011110000111000011110101110101001111010100011011010010011";
    private ulong crcPolynomial;

    // ManualResetEvent instances signal completion.
    private static ManualResetEvent connectDone = new ManualResetEvent(false);
    private static ManualResetEvent sendDone = new ManualResetEvent(false);
    private static ManualResetEvent receiveDone = new ManualResetEvent(false);

    // Receive transform queue
    public readonly static Queue<Action> ReceiveTransformQueue = new Queue<Action>();

    public TCPClient OpenIGTLinkConnection;
    public StateObject connectionInfo;
    private bool connectionStarted = false;

    // Use this for initialization
    void Start()
    {
        // Initialize CRC Generator
        crcGenerator = new CRC64();
        crcPolynomial = Convert.ToUInt64(crcPolynomialBinary, 2);
        crcGenerator.Init(crcPolynomial);

        // Load settings from file
        FileInfo iniFile = new FileInfo("config.txt");
        StreamReader reader = iniFile.OpenText();
        string text = reader.ReadLine();
        if (text != null) ipString = text;

        text = reader.ReadLine();
        if (text != null) port = int.Parse(text);

        // TODO: Connect on prompt rather than application start
        OpenIGTLinkConnection = new TCPClient(ipString, port);
        connectionStarted = OpenIGTLinkConnection.connected;
        connectionInfo = OpenIGTLinkConnection.connectionInfo;

        //begin reading data passed from TCPClient connection
        RestartReadThread();
    }

    // Update is called once per frame
    void Update()
    {

        // Repeat every msDelay millisecond
        if (totalTime * 1000 > msDelay)
        {
            if (connectionStarted)
            {
                // Send Transform Data if Flag is on
                foreach (GameObject gameObject in GameObjects)
                {
                    if (gameObject.GetComponent<OpenIGTLinkFlag>().SendTransform)
                    {
                        SendTransformMessage(gameObject.transform);
                    }

                    if (gameObject.GetComponent<OpenIGTLinkFlag>().SendPoint & gameObject.GetComponent<OpenIGTLinkFlag>().GetMovedPosition())
                    {
                        SendPointMessage(gameObject.transform);
                    }
                }

                // Perform all queued Receive Transforms
                while (ReceiveTransformQueue.Count > 0)
                {
                    ReceiveTransformQueue.Dequeue().Invoke();
                }
            }
            // Reset timer
            totalTime = 0f;
        }
        totalTime = totalTime + Time.deltaTime;
    }

    void OnApplicationQuit()
    {
        // Release the socket.
        OpenIGTLinkConnection.StopExchangeThread();
        OpenIGTLinkConnection = null;
        // Close threads
        StopReadThread();
    }

    public void RestartReadThread()
    {
#if UNITY_EDITOR
        if (receiveThread != null) StopReadThread();
        readStopRequested = false;
        readThread = new System.Threading.Thread(ReadData);
        readThread.Start();
#else
        if (readTask != null) StopReadThread();
        readStopRequested = false;
        readTask = Task.Run(() => ReadData());
#endif
    }
    public void StopReadThread()
    {
        readStopRequested = true;

#if UNITY_EDITOR
        if (readThread != null)
        {
            readThread.Abort();
            readThread = null;
        }
#else
        if (readTask != null)
        {
            readTask.Wait();
            readTask = null;
        }
#endif
    }

    private void ReadData()
    {
        while(!StopReadThread)
        {
            //collect values for if statements, lock from other threads while accessing
            lock (connectionInfo)
            {
                int totalBytesRead = connectionInfo.totalBytesRead;
                int byteListCount = connectionInfo.byteList.Count;
            }

            //do nothing until there is data to be read
            if (totalBytesRead > 0)
            {
                bool moreToRead = true;
            }

            //continue reading until all data is read, then wait for more data
            while (moreToRead)
            {
                // Read the header and determine data type
                if (!connectionInfo.headerRead & byteListCount > 0)
                {
                    lock(connectionInfo.byteList)
                    {
                        string dataType = Encoding.ASCII.GetString(connectionInfo.byteList.GetRange(2, 12).ToArray()).Replace("\0", string.Empty);
                        connectionInfo.name = Encoding.ASCII.GetString(connectionInfo.byteList.GetRange(14, 20).ToArray()).Replace("\0", string.Empty);
                        byte[] dataSizeBytes = connectionInfo.byteList.GetRange(42, 8).ToArray();
                    }

                    if (BitConverter.IsLittleEndian)
                    {
                        Array.Reverse(dataSizeBytes);
                    }
                    connectionInfo.dataSize = BitConverter.ToInt32(dataSizeBytes, 0) + 58; //58 = header size

                    Debug.Log(String.Format("Data is of type {0} with name {1} and size {2}", dataType, connectionInfo.name, connectionInfo.dataSize));

                    if (dataType.Equals("IMAGE"))
                    {
                        connectionInfo.dataType = StateObject.DataTypes.IMAGE;
                    }
                    else if (dataType.Equals("TRANSFORM"))
                    {
                        connectionInfo.dataType = StateObject.DataTypes.TRANSFORM;
                    }
                    else
                    {
                        //not a datatype we are looking for
                        moreToRead = false;
                        receiveDone.Set();
                        return;
                    }
                    connectionInfo.headerRead = true;
                }

               
                //if byteList contains just enough elements to cover the datasize of the current message
                if (totalBytesRead == connectionInfo.dataSize)
                {
                    // All the data has arrived; put it in response.
                    if (byteListCount > 1)
                    {
                        // Send off to interpret data based on data type
                        if (connectionInfo.dataType == StateObject.DataTypes.TRANSFORM)
                        {
                            OpenIGTLinkConnect.ReceiveTransformQueue.Enqueue(() =>
                            {
                                lock(connectionInfo.byteList)
                                {
                                    StartCoroutine(ReceiveTransformMessage(connectionInfo.byteList.ToArray(), connectionInfo.name));
                                }   
                            });
                        }
                    }
                    // Signal that all bytes have been received.
                    moreToRead = false;
                    receiveDone.Set();
                }
                // More data than expected has arrived; move current message to queue and start on next message.
                else if ((totalBytesRead > connectionInfo.dataSize) & (byteListCount > connectionInfo.dataSize) & connectionInfo.dataSize > 0)
                {

                    // Send off to interpret data based on data type
                    if (connectionInfo.dataType == StateObject.DataTypes.TRANSFORM)
                    {
                        OpenIGTLinkConnect.ReceiveTransformQueue.Enqueue(() =>
                        {
                            try
                            {
                                lock(connectionInfo.byteList)
                                {
                                    StartCoroutine(ReceiveTransformMessage(connectionInfo.byteList.GetRange(0, connectionInfo.dataSize).ToArray(), connectionInfo.name));
                                }
                                
                            }
                            catch (Exception e)
                            {
                                lock(connectionInfo) //accessing both byteList and totalBytesRead, lock entire state object
                                {
                                    Debug.Log(String.Format("{0} receiving {1} with total {2}", connectionInfo.byteList.Count, connectionInfo.dataSize, connectionInfo.totalBytesRead));
                                }

                                Debug.Log(String.Format(e.ToString()));
                            }
                        });
                    }
                    lock (connectionInfo)
                    {
                        connectionInfo.byteList.RemoveRange(0, connectionInfo.dataSize); 
                        connectionInfo.totalBytesRead = connectionInfo.totalBytesRead - connectionInfo.dataSize;  
                    }
                    
                    connectionInfo.dataSize = 0;
                    connectionInfo.name = "";
                    connectionInfo.headerRead = false;
                }
                // not enough bytes have been received to cover the current datasize, wait until enough have been recieved
                else
                {
                    //header is already read
                    //loop until enough bytes to complete message
                    moreToRead = true;
                }
            }
        }
        
    }
}
// ------------ Receive Functions ------------ 
// --- Receive Transform --- 
IEnumerator ReceiveTransformMessage(byte[] data, string transformName)
{
    // Find Game Objects with Transform Name and determine if they should be updated
    string objectName;
    foreach (GameObject gameObject in GameObjects)
    {
        // Could be a bit more efficient
        if (gameObject.name.Length > 20)
        {
            objectName = gameObject.name.Substring(0, 20);
        }
        else
        {
            objectName = gameObject.name;
        }

        if (objectName.Equals(transformName) & gameObject.GetComponent<OpenIGTLinkFlag>().ReceiveTransform)
        {
            // Transform Matrix starts from byte 58 until 106
            // Extract transform matrix
            byte[] matrixBytes = new byte[4];
            float[] m = new float[12];
            for (int i = 0; i < 12; i++)
            {
                Buffer.BlockCopy(data, 58 + i * 4, matrixBytes, 0, 4);
                if (BitConverter.IsLittleEndian)
                {
                    Array.Reverse(matrixBytes);
                }

                m[i] = BitConverter.ToSingle(matrixBytes, 0);

            }

            // Slicer units are in millimeters, Unity is in meters, so convert accordingly
            // Definition for Matrix4x4 is extended from SteamVR
            Matrix4x4 matrix = new Matrix4x4();
            matrix.SetRow(0, new Vector4(m[0], m[3], m[6], m[9] / scaleMultiplier));
            matrix.SetRow(1, new Vector4(m[1], m[4], m[7], m[10] / scaleMultiplier));
            matrix.SetRow(2, new Vector4(m[2], m[5], m[8], m[11] / scaleMultiplier));
            matrix.SetRow(3, new Vector4(0.0f, 0.0f, 0.0f, 1.0f));

            Matrix4x4 IJKToRAS = new Matrix4x4();
            IJKToRAS.SetRow(0, new Vector4(-1.0f, 0, 0, 0));
            IJKToRAS.SetRow(1, new Vector4(0, -1.0f, 0, 2));
            IJKToRAS.SetRow(2, new Vector4(0, 0, 1.0f, 0));
            IJKToRAS.SetRow(3, new Vector4(0.0f, 0.0f, 0.0f, 1.0f));

            Matrix4x4 matrixRAS = matrix * IJKToRAS;

            Vector3 translation = matrix.GetColumn(3);
            gameObject.transform.localPosition = new Vector3(-translation.x, translation.y, translation.z);
            Vector3 eulerAngles = matrix.GetRotation().eulerAngles;
            gameObject.transform.localRotation = Quaternion.Euler(eulerAngles.x, -eulerAngles.y, -eulerAngles.z);
        }
    }
    // Place this inside the loop if you only want to perform one loop per update cycle
    yield return null;
}

// ------------ Send Functions ------------ 
// --- Send Transform --- 
void SendTransformMessage(Transform objectTransform)
{
    // Header
    // Header information:
    // Version 1
    // Type Transform
    // Device Name 
    // Time 0
    // Body size 30 bytes
    // 0001 Type:5452414E53464F524D000000 Name:4F63756C757352696674506F736974696F6E0000 00000000000000000000000000000030

    string hexHeader = "0001" + StringToHexString("TRANSFORM", 12) + StringToHexString(objectTransform.name, 20) + "00000000000000000000000000000030";

    // Body
    string m00Hex;
    string m01Hex;
    string m02Hex;
    string m03Hex;
    string m10Hex;
    string m11Hex;
    string m12Hex;
    string m13Hex;
    string m20Hex;
    string m21Hex;
    string m22Hex;
    string m23Hex;

    Matrix4x4 matrix = Matrix4x4.TRS(objectTransform.localPosition, objectTransform.localRotation, objectTransform.localScale);

    float m00 = matrix.GetRow(0)[0];
    byte[] m00Bytes = BitConverter.GetBytes(m00);
    float m01 = matrix.GetRow(0)[1];
    byte[] m01Bytes = BitConverter.GetBytes(m01);
    float m02 = matrix.GetRow(0)[2];
    byte[] m02Bytes = BitConverter.GetBytes(m02);
    float m03 = matrix.GetRow(0)[3];
    byte[] m03Bytes = BitConverter.GetBytes(m03 * scaleMultiplier);

    float m10 = matrix.GetRow(1)[0];
    byte[] m10Bytes = BitConverter.GetBytes(m10);
    float m11 = matrix.GetRow(1)[1];
    byte[] m11Bytes = BitConverter.GetBytes(m11);
    float m12 = matrix.GetRow(1)[2];
    byte[] m12Bytes = BitConverter.GetBytes(m12);
    float m13 = matrix.GetRow(1)[3];
    byte[] m13Bytes = BitConverter.GetBytes(m13 * scaleMultiplier);

    float m20 = matrix.GetRow(2)[0];
    byte[] m20Bytes = BitConverter.GetBytes(m20);
    float m21 = matrix.GetRow(2)[1];
    byte[] m21Bytes = BitConverter.GetBytes(m21);
    float m22 = matrix.GetRow(2)[2];
    byte[] m22Bytes = BitConverter.GetBytes(m22);
    float m23 = matrix.GetRow(2)[3];
    byte[] m23Bytes = BitConverter.GetBytes(m23 * scaleMultiplier);

    if (BitConverter.IsLittleEndian)
    {
        Array.Reverse(m00Bytes);
        Array.Reverse(m01Bytes);
        Array.Reverse(m02Bytes);
        Array.Reverse(m03Bytes);
        Array.Reverse(m10Bytes);
        Array.Reverse(m11Bytes);
        Array.Reverse(m12Bytes);
        Array.Reverse(m13Bytes);
        Array.Reverse(m20Bytes);
        Array.Reverse(m21Bytes);
        Array.Reverse(m22Bytes);
        Array.Reverse(m23Bytes);
    }
    m00Hex = BitConverter.ToString(m00Bytes).Replace("-", "");
    m01Hex = BitConverter.ToString(m01Bytes).Replace("-", "");
    m02Hex = BitConverter.ToString(m02Bytes).Replace("-", "");
    m03Hex = BitConverter.ToString(m03Bytes).Replace("-", "");
    m10Hex = BitConverter.ToString(m10Bytes).Replace("-", "");
    m11Hex = BitConverter.ToString(m11Bytes).Replace("-", "");
    m12Hex = BitConverter.ToString(m12Bytes).Replace("-", "");
    m13Hex = BitConverter.ToString(m13Bytes).Replace("-", "");
    m20Hex = BitConverter.ToString(m20Bytes).Replace("-", "");
    m21Hex = BitConverter.ToString(m21Bytes).Replace("-", "");
    m22Hex = BitConverter.ToString(m22Bytes).Replace("-", "");
    m23Hex = BitConverter.ToString(m23Bytes).Replace("-", "");

    string body = m00Hex + m10Hex + m20Hex + m01Hex + m11Hex + m21Hex + m02Hex + m12Hex + m22Hex + m03Hex + m13Hex + m23Hex;

    ulong crcULong = crcGenerator.Compute(StringToByteArray(body), 0, 0);
    CRC = crcULong.ToString("X16");

    string hexmsg = hexHeader + CRC + body;

    // Encode the data string into a byte array.
    byte[] msg = StringToByteArray(hexmsg);

    // Send the data through the TCP client.
    OpenIGTLinkConnection.SendMessage(msg);
}

// --- Send Point --- 
void SendPointMessage(Transform objectTransform)
{
    // Header
    // Header information:
    // Version 1
    // Type Point
    // Device Name 
    // Time 0

    // Size 88 for one point, no support for full point list yet
    string hexHeader = "0001" + StringToHexString("POINT", 12) + StringToHexString(objectTransform.name, 20) + "00000000000000000000000000000088";

    // Body
    string xHex;
    string yHex;
    string zHex;

    float x = -objectTransform.localPosition.x * scaleMultiplier;
    float y = objectTransform.localPosition.y * scaleMultiplier;
    float z = objectTransform.localPosition.z * scaleMultiplier;

    byte[] xBytes = BitConverter.GetBytes(x);
    byte[] yBytes = BitConverter.GetBytes(y);
    byte[] zBytes = BitConverter.GetBytes(z);

    if (BitConverter.IsLittleEndian)
    {
        Array.Reverse(xBytes);
        Array.Reverse(yBytes);
        Array.Reverse(zBytes);
    }
    xHex = BitConverter.ToString(xBytes).Replace("-", "");
    yHex = BitConverter.ToString(yBytes).Replace("-", "");
    zHex = BitConverter.ToString(zBytes).Replace("-", "");

    // Default Slicer Fiducial Color
    string pointHeader = StringToHexString("TargetList-1", 64) + StringToHexString("GROUP_0", 32) + "FF0000FF";
    string diameter = "42340000";
    string imageID = StringToHexString("IMAGE_0", 20);

    string body = pointHeader + xHex + yHex + zHex + diameter + imageID;

    ulong crcULong = crcGenerator.Compute(StringToByteArray(body), 0, 0);
    CRC = crcULong.ToString("X16");

    string hexmsg = hexHeader + CRC + body;

    // Encode the data string into a byte array.
    byte[] msg = StringToByteArray(hexmsg);

    // Send the data through the TCP client.
    OpenIGTLinkConnection.SendMessage(msg);
}

// --- Send Helpers ---
static string StringToHexString(string inputString, int sizeInBytes)
{
    if (inputString.Length > sizeInBytes)
    {
        inputString = inputString.Substring(0, sizeInBytes);
    }

    byte[] ba = Encoding.Default.GetBytes(inputString);
    string hexString = BitConverter.ToString(ba);
    hexString = hexString.Replace("-", "");
    hexString = hexString.PadRight(sizeInBytes * 2, '0');
    return hexString;
}

static byte[] StringToByteArray(string hex)
{
    byte[] arr = new byte[hex.Length >> 1];

    for (int i = 0; i < (hex.Length >> 1); ++i)
    {
        arr[i] = (byte)((GetHexVal(hex[i << 1]) << 4) + (GetHexVal(hex[(i << 1) + 1])));
    }

    return arr;
}

static string ByteArrayToString(byte[] ba)
{
    StringBuilder hex = new StringBuilder(ba.Length * 2);
    foreach (byte b in ba)
        hex.AppendFormat("{0:x2}", b);
    return hex.ToString();
}

static int GetHexVal(char hex)
{
    int val = (int)hex;
    //For uppercase:
    return val - (val < 58 ? 48 : 55);
    //For lowercase:
    //return val - (val < 58 ? 48 : 87);
}


public class CRC64
{
    private ulong[] _table;

    private ulong CmTab(int index, ulong poly)
    {
        ulong retval = (ulong)index;
        ulong topbit = (ulong)1L << (64 - 1);
        ulong mask = 0xffffffffffffffffUL;

        retval <<= (64 - 8);
        for (int i = 0; i < 8; i++)
        {
            if ((retval & topbit) != 0)
                retval = (retval << 1) ^ poly;
            else
                retval <<= 1;
        }
        return retval & mask;
    }

    private ulong[] GenStdCrcTable(ulong poly)
    {
        ulong[] table = new ulong[256];
        for (var i = 0; i < 256; i++)
            table[i] = CmTab(i, poly);
        return table;
    }

    private ulong TableValue(ulong[] table, byte b, ulong crc)
    {
        return table[((crc >> 56) ^ b) & 0xffUL] ^ (crc << 8);
    }

    public void Init(ulong poly)
    {
        _table = GenStdCrcTable(poly);
    }

    public ulong Compute(byte[] bytes, ulong initial, ulong final)
    {
        ulong current = initial;
        for (var i = 0; i < bytes.Length; i++)
        {
            current = TableValue(_table, bytes[i], current);
        }
        return current ^ final;

    }

}

// Receive Object
public class StateObject
{

#if UNITY_EDITOR
    // Client socket.
    public Socket workSocket = null;
#else
    public StreamSocket workSocket = null;
#endif
    // Size of receive buffer.
    public const int BufferSize = 4194304;
    // Receive buffer.
    public byte[] buffer = new byte[BufferSize];
    // Received data string.
    //public StringBuilder sb = new StringBuilder();
    public List<Byte> byteList = new List<Byte>();
    // OpenIGTLink Data Type
    public enum DataTypes { IMAGE = 0, TRANSFORM };
    public DataTypes dataType;
    // Header read or not
    public bool headerRead = false;
    // Data Size read from header
    public int dataSize = -1;
    // Bytes of data read so far
    public int totalBytesRead = 0;
    // Transform Name
    public string name;
}

public class TCPClient
{
    public string hostName;
    public string port;
    public bool connected = false;
    public StateObject connectionInfo;
    private StreamWriter writer;
    private StreamReader reader;
    private bool exchangeStopRequested = false;
    private bool exchanging = false;
    public byte[] msg = null;

#if !UNITY_EDITOR
    private Task exchangeDataTask;
#else
    private Thread exchangeDataThread;
#endif

    //when creating new TCPClient pass ipAddress and host name
    public TCPClient(string ipString, string portString)
    {
        hostName = ipString;
        port = portString;
        SetupClient();
    }

    public void SetupClient()
    {
#if UNITY_EDITOR
        try
        {
            if(exchangeDataThread != null) StopExchangeThread();

            TcpClient socket = new System.Net.Sockets.TcpClient(host, Int32.Parse(port));
            stream = socket.GetStream();
            reader = new StreamReader(stream);
            writer = new StreamWriter(stream) { AutoFlush = true };

            RestartExchangeThread();
            connected = true;
        }
        catch (Exception e)
        {
            Debug.Log(String.Format(e.ToString()));
        }
        
#else
        try
        {
            if (exchangeDataTask != null) StopExchangeThread();

            StreamSocket socket = new Windows.Networking.Sockets.StreamSocket();
            Windows.Networking.HostName serverHost = new Windows.Networking.HostName(hostName);
            await socket.ConnectAsync(serverHost, port);

            Stream streamOut = socket.OutputStream.AsStreamForWrite();
            writer = new StreamWriter(streamOut) { AutoFlush = true };

            Stream streamIn = socket.InputStream.AsStreamForRead();
            reader = new StreamReader(streamIn);

            RestartExchangeThread();
            connected = true;
        }
        catch (Exception e)
        {
            Debug.Log(String.Format(e.ToString()));
        }
#endif

        connectionInfo.workSocket = socket;
    }

    public void SendMessage(byte[] msgToSend)
    {
        //change msg from null
        msg = msgToSend;
    }

    public void ExchangeData()
    {
        while (!exchangeStopRequested)
        {
            if (writer == null || reader == null) continue; //Streams not setup yet
            exchanging = true;

#if UNITY_EDITOR
            
            //read data and determine number of bytes read
            stream.Read(client.buffer, 0, client.BufferSize);
            int numberBytesRead = client.buffer.GetLength();
            lock(connectionInfo.totalBytesRead)
            {
                connectionInfo.totalBytesRead += numberBytesRead;
            }

            //copy data from buffer, append to byteList
            byte[] bytesRead = new Byte[numberBytesRead];
            Array.Copy(client.buffer, bytesRead, numberBytesRead);
            lock (connectionInfo.byteList)
            {
                connectionInfo.byteList.AddRange(bytesRead);
            }

            if (msg != null) //if there is a messge to send
            {
                stream.Write(msg, 0, msg.Length);
                //clear message after sending
                msg = null;
            }

#else
            //read data and determine number of bytes read
            reader.Read(connectionInfo.buffer, 0, connectionInfo.BufferSize);
            int numberBytesRead = connectionInfo.buffer.GetLength();
            lock(connectionInfo.totalBytesRead)
            {
                connectionInfo.totalBytesRead += numberBytesRead;
            }

            //copy data from buffer, append to byteList
            byte[] bytesRead = new Byte[numberBytesRead];
            Array.Copy(connectionInfo.buffer, bytesRead, numberBytesRead);
            lock (connectionInfo.byteList)
            {
                connectionInfo.byteList.AddRange(bytesRead);
            }

            if (msg != null) //if there is a messge to send
            {
                writer.Write(msg, 0, msg.Length);
                //clear message after sending
                msg = null;
            }
#endif

            exchanging = false;
        }
    }

    public void RestartExchangeThread()
    {
#if UNITY_EDITOR
        if (exchangeThread != null) StopExchangeThread();
        exchangeStopRequested = false;
        exchangeThread = new System.Threading.Thread(ExchangeData);
        exchangeThread.Start();
#else
        if (exchangeTask != null) StopExchangeThread();
        exchangeStopRequested = false;
        exchangeTask = Task.Run(() => ExchangeData());
#endif
    }
    public void StopExchangeThread()
    {
        exchangeStopRequested = true;

#if UNITY_EDITOR
        if (exchangeThread != null)
        {
            exchangeThread.Abort();
            stream.Close();
            socket.Close();
            writer.Close();
            reader.Close();

            stream = null;
            exchangeThread = null;
        }
#else
        if (exchangeTask != null)
        {
            exchangeTask.Wait();
            socket.Dispose();
            writer.Dispose();
            reader.Dispose();

            socket = null;
            exchangeTask = null;
        }
#endif
        writer = null;
        reader = null;
    }


}