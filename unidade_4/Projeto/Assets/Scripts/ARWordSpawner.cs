using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using Unity.Collections;
using UnityEngine;
using UnityEngine.Networking;
using UnityEngine.XR.ARFoundation;
using UnityEngine.XR.ARSubsystems;
using OpenCVForUnity.CoreModule;
using OpenCVForUnity.ImgprocModule;
using OpenCVForUnity.UnityUtils;
using OpenCVForUnity.TextModule; // OCRTesseract

[Serializable]
public class WordPrefab { public string word; public GameObject prefab; }

[RequireComponent(typeof(ARCameraManager))]
public class ARWordSpawner : MonoBehaviour
{
    [Header("OCRTesseract settings")]
    public string tessDataRelativePath = "tessdata"; // inside StreamingAssets
    public string language = "por";
    [Range(0, 100)] public int minConfidence = 50;

    [Header("Detection settings")]
    public float detectIntervalSeconds = 2f;
    public int minTextLength = 3;

    [Header("Word -> Prefab mappings")]
    public List<WordPrefab> mappings = new List<WordPrefab>();

    [Header("AR placement")]
    public ARRaycastManager arRaycastManager;
    public Camera arCamera;
    public float fallbackDistance = 1.5f;

    [Header("Debug / Test")]
    public bool runTestOnStart = false; // se true, roda o teste com Resources/test_capivara.png
    public bool saveDebugImages = false; // salva imagens que falharam
    public float resizeScale = 1.5f; // escala de resize para melhorar OCR

    // runtime
    private OCRTesseract ocr;
    private Dictionary<string, GameObject> mapDict;
    private bool processing = false;
    private float lastDetectTime = -999f;
    static List<ARRaycastHit> s_Hits = new List<ARRaycastHit>();


    [Header("Capture mode")]
    public bool useRenderedFrame = false; // coloque true para capturar o que a Unity renderiza (sprites/UI)

    public int renderedCaptureWidth = 640;   // resolução da captura renderizada (ajuste pra perf)
    public int renderedCaptureHeight = 360;

    void Awake()
    {
        if (arCamera == null) arCamera = Camera.main;
        if (arRaycastManager == null) arRaycastManager = FindObjectOfType<ARRaycastManager>();

        mapDict = new Dictionary<string, GameObject>(StringComparer.OrdinalIgnoreCase);
        foreach (var wp in mappings)
            if (!string.IsNullOrWhiteSpace(wp.word) && wp.prefab != null)
                mapDict[wp.word.ToLowerInvariant()] = wp.prefab;
    }

    void Start()
    {
        StartCoroutine(InitializeAndSubscribe());
    }

    private IEnumerator InitializeAndSubscribe()
    {
        string streamingTessPath = Path.Combine(Application.streamingAssetsPath, tessDataRelativePath);
        string datapathToUse = streamingTessPath;

#if UNITY_ANDROID && !UNITY_EDITOR
        // copy traineddata to persistent path so native code can open it
        string persistentTess = Path.Combine(Application.persistentDataPath, tessDataRelativePath);
        if (!Directory.Exists(persistentTess)) Directory.CreateDirectory(persistentTess);
        string fileName = language + ".traineddata";
        string persistentFile = Path.Combine(persistentTess, fileName);
        if (!File.Exists(persistentFile))
        {
            string uri = Path.Combine(Application.streamingAssetsPath, tessDataRelativePath, fileName);
            using (UnityWebRequest uwr = UnityWebRequest.Get(uri))
            {
                uwr.timeout = 30;
                yield return uwr.SendWebRequest();
#if UNITY_2020_1_OR_NEWER
                if (uwr.result != UnityWebRequest.Result.Success)
#else
                if (uwr.isNetworkError || uwr.isHttpError)
#endif
                {
                    Debug.LogWarning($"[OCR] Falha ao ler {uri}: {uwr.error}");
                }
                else
                {
                    try { File.WriteAllBytes(persistentFile, uwr.downloadHandler.data); Debug.Log("[OCR] Copiado " + fileName + " para persistentDataPath."); }
                    catch (Exception ex) { Debug.LogWarning("[OCR] Erro ao salvar traineddata: " + ex.Message); }
                }
            }
        }
        datapathToUse = persistentTess;
#endif

        Debug.Log("[OCR] datapath que será usado: " + datapathToUse);

        // lista arquivos em datapath (diagnóstico)
        try
        {
            if (Directory.Exists(datapathToUse))
            {
                var files = Directory.GetFiles(datapathToUse);
                Debug.Log($"[OCR] Found {files.Length} files in tessdata:");
                foreach (var f in files) Debug.Log("   " + Path.GetFileName(f));
            }
            else Debug.LogWarning("[OCR] tessdata path não existe: " + datapathToUse);
        }
        catch (Exception e) { Debug.LogWarning("[OCR] erro ao listar tessdata: " + e.Message); }

        try
        {
            ocr = OCRTesseract.create(datapathToUse, language);
            Debug.Log("[OCR] OCRTesseract criado com sucesso.");
        }
        catch (Exception e)
        {
            Debug.LogWarning("[OCR] Falha ao inicializar OCRTesseract (binário nativo pode estar ausente): " + e.Message);
            ocr = null;
        }

        var camMgr = GetComponent<ARCameraManager>();
        if (camMgr != null) camMgr.frameReceived += OnCameraFrameReceived;
        else Debug.LogError("[OCR] ARCameraManager não encontrado.");

        if (runTestOnStart) RunTestImage();
        yield break;
    }

    void OnDestroy()
    {
        var camMgr = GetComponent<ARCameraManager>();
        if (camMgr != null) camMgr.frameReceived -= OnCameraFrameReceived;

        if (ocr != null)
        {
            try { ocr.Dispose(); } catch { }
            ocr = null;
        }
    }

    private void OnCameraFrameReceived(ARCameraFrameEventArgs args)
    {
        if (Time.time - lastDetectTime < detectIntervalSeconds) return;
        if (processing) return;

        var camMgr = GetComponent<ARCameraManager>();
        if (camMgr == null) return;

        if (!camMgr.TryAcquireLatestCpuImage(out XRCpuImage image)) return;

        lastDetectTime = Time.time;
        processing = true;
        StartCoroutine(ProcessFrameCoroutine(image));
    }

    private IEnumerator ProcessFrameCoroutine(XRCpuImage cpuImage)
    {
        // Tentar duas transformações se necessário: primeiro None, depois MirrorY se as imagens vierem estranhas
        XRCpuImage.Transformation[] tryTransforms = new XRCpuImage.Transformation[] {
        XRCpuImage.Transformation.None,
        XRCpuImage.Transformation.MirrorY
    };

        bool success = false;
        string usedTransformName = "None";

        foreach (var trans in tryTransforms)
        {
            // conversão para RGBA32 com a transformação testada
            var conv = new XRCpuImage.ConversionParams
            {
                inputRect = new RectInt(0, 0, cpuImage.width, cpuImage.height),
                outputDimensions = new Vector2Int(cpuImage.width, cpuImage.height),
                outputFormat = TextureFormat.RGBA32,
                transformation = trans
            };

            int size = cpuImage.GetConvertedDataSize(conv);
            var buffer = new NativeArray<byte>(size, Allocator.Temp);
            bool converted = true;
            try
            {
                cpuImage.Convert(conv, buffer);
            }
            catch (Exception e)
            {
                Debug.LogWarning("[OCR] Convert falhou com transform=" + trans + " : " + e.Message);
                converted = false;
            }

            if (!converted)
            {
                buffer.Dispose();
                continue;
            }

            Texture2D tex = new Texture2D(conv.outputDimensions.x, conv.outputDimensions.y, conv.outputFormat, false);
            tex.LoadRawTextureData(buffer);
            tex.Apply();
            buffer.Dispose();

            // Converte para Mat RGBA
            Mat rgba = new Mat(tex.height, tex.width, CvType.CV_8UC4);
            Utils.texture2DToMat(tex, rgba);

            // Salva a imagem original RGBA para diagnóstico
            SaveMatAsPngColor(rgba, "orig_trans_" + trans.ToString());

            // Pré-processamento: gray, resize, equalize, blur
            Mat gray = new Mat();
            Imgproc.cvtColor(rgba, gray, Imgproc.COLOR_RGBA2GRAY);

            if (Math.Abs(resizeScale - 1f) > 0.001f)
            {
                int newW = Mathf.RoundToInt(gray.cols() * resizeScale);
                int newH = Mathf.RoundToInt(gray.rows() * resizeScale);
                Imgproc.resize(gray, gray, new Size(newW, newH));
            }

            Imgproc.equalizeHist(gray, gray);
            Imgproc.GaussianBlur(gray, gray, new Size(3, 3), 0);

            // salvar grayscale (diagnóstico)
            SaveMatAsPngGray(gray, "gray_trans_" + trans.ToString());

            // threshold invertido e dilate (ajustável)
            Mat bin = new Mat();
            Imgproc.adaptiveThreshold(gray, bin, 255, Imgproc.ADAPTIVE_THRESH_GAUSSIAN_C, Imgproc.THRESH_BINARY_INV, 15, 8);
            Imgproc.dilate(bin, bin, Imgproc.getStructuringElement(Imgproc.MORPH_RECT, new Size(2, 2)));

            // salvar binária (diagnóstico)
            SaveMatAsPngGray(bin, "bin_trans_" + trans.ToString());

            // Tentar OCR com esse bin
            string ocrResult = "";
            if (ocr != null)
            {
                try
                {
                    ocrResult = ocr.run(bin, 0);
                }
                catch (Exception e)
                {
                    Debug.LogWarning("[OCR] run() falhou: " + e.Message);
                    ocrResult = "";
                }
            }
            else
            {
                ocrResult = DummyOCRSimulation();
            }

            Debug.Log($"[OCR] transform={trans} -> result raw: '{ocrResult}'");

            // se detectou texto, usamos este transform e saimos do loop
            if (!string.IsNullOrEmpty(ocrResult) && ocrResult.Trim().Length >= minTextLength)
            {
                success = true;
                usedTransformName = trans.ToString();
                // processa match com mapa
                string lower = ocrResult.ToLowerInvariant();
                foreach (var kv in mapDict)
                {
                    if (lower.Contains(kv.Key.ToLowerInvariant()))
                    {
                        Debug.Log($"[OCR] Palavra detectada: '{kv.Key}' -> instanciando prefab.");
                        SpawnPrefabForWord(kv.Value);
                        break;
                    }
                }
                // cleanup mats/textures
                bin.Dispose();
                gray.Dispose();
                rgba.Dispose();
                Destroy(tex);
                break;
            }

            // se não detectou, descarta e tenta próxima transformação
            bin.Dispose();
            gray.Dispose();
            rgba.Dispose();
            Destroy(tex);

            // continue loop para próxima transformação (MirrorY)
        } // fim foreach transforms

        cpuImage.Dispose();

        if (!success)
        {
            Debug.LogWarning("[OCR] nenhuma transformação retornou texto suficiente. Verifique as imagens salvas em persistentPath/ocr_debug.");
        }
        else
        {
            Debug.Log("[OCR] teve sucesso usando transform: " + usedTransformName);
        }

        processing = false;
        yield return null;
    }

    // helpers de salvamento
    private void SaveMatAsPngColor(Mat colorMat, string nameTag)
    {
        try
        {
            // converte Mat CV_8UC4 para Texture2D RGBA32
            Texture2D tex = new Texture2D(colorMat.cols(), colorMat.rows(), TextureFormat.RGBA32, false);
            Utils.matToTexture2D(colorMat, tex);
            byte[] png = tex.EncodeToPNG();
            string dir = Path.Combine(Application.persistentDataPath, "ocr_debug");
            if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
            string path = Path.Combine(dir, $"{nameTag}_{DateTime.Now.ToString("yyyyMMdd_HHmmss")}.png");
            File.WriteAllBytes(path, png);
            Debug.Log("[OCR] saved color debug: " + path);
            UnityEngine.Object.Destroy(tex);
        }
        catch (Exception e)
        {
            Debug.LogWarning("[OCR] SaveMatAsPngColor falhou: " + e.Message);
        }
    }

    private void SaveMatAsPngGray(Mat grayMat, string nameTag)
    {
        try
        {
            // converte CV_8UC1 para CV_8UC4 antes de salvar
            Mat color = new Mat();
            Imgproc.cvtColor(grayMat, color, Imgproc.COLOR_GRAY2RGBA);
            SaveMatAsPngColor(color, nameTag);
            color.Dispose();
        }
        catch (Exception e)
        {
            Debug.LogWarning("[OCR] SaveMatAsPngGray falhou: " + e.Message);
        }
    }

    private string DummyOCRSimulation()
    {
        if (UnityEngine.Random.value < 0.02f) return "capivara";
        return "";
    }

    private void SpawnPrefabForWord(GameObject prefab)
    {
        if (prefab == null) return;
        Vector3 spawnPos;
        Quaternion spawnRot = Quaternion.identity;
        Vector2 screenCenter = new Vector2(Screen.width * 0.5f, Screen.height * 0.5f);
        if (arRaycastManager != null && arRaycastManager.Raycast(screenCenter, s_Hits, TrackableType.Planes))
        {
            var hitPose = s_Hits[0].pose;
            spawnPos = hitPose.position;
            spawnRot = hitPose.rotation;
        }
        else
        {
            spawnPos = arCamera.transform.position + arCamera.transform.forward * fallbackDistance;
            spawnRot = Quaternion.LookRotation(arCamera.transform.forward, Vector3.up);
        }
        Instantiate(prefab, spawnPos, spawnRot);
    }

    private void SaveDebugImage(Mat mat, string tag)
    {
        try
        {
            string dir = Path.Combine(Application.persistentDataPath, "ocr_debug");
            if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
            string filename = $"{tag}_{DateTime.Now.ToString("yyyyMMdd_HHmmss")}.png";
            string path = Path.Combine(dir, filename);

            // converter Mat (grayscale/binary) para Texture2D
            Texture2D t = new Texture2D(mat.cols(), mat.rows(), TextureFormat.R8, false);
            Utils.matToTexture2D(mat, t);
            byte[] png = t.EncodeToPNG();
            File.WriteAllBytes(path, png);
            Debug.Log("[OCR] Debug image saved: " + path);
            UnityEngine.Object.Destroy(t);
        }
        catch (Exception e) { Debug.LogWarning("[OCR] falha ao salvar debug image: " + e.Message); }
    }

    [ContextMenu("Run Test Image (Resources/test_capivara.png)")]
    public void RunTestImage()
    {
        StartCoroutine(RunTestImageCoroutine());
    }

    private IEnumerator RunTestImageCoroutine()
    {
        Texture2D test = Resources.Load<Texture2D>("test_capivara");
        if (test == null) { Debug.LogWarning("[OCR] Coloque Assets/Resources/test_capivara.png para teste."); yield break; }

        // converte para Mat e roda OCR com mesmo pré-process
        Mat rgba = new Mat(test.height, test.width, CvType.CV_8UC4);
        Utils.texture2DToMat(test, rgba);
        Mat gray = new Mat(); Imgproc.cvtColor(rgba, gray, Imgproc.COLOR_RGBA2GRAY);
        if (resizeScale != 1f) Imgproc.resize(gray, gray, new Size(gray.cols() * resizeScale, gray.rows() * resizeScale));
        Imgproc.equalizeHist(gray, gray);
        Imgproc.GaussianBlur(gray, gray, new Size(3, 3), 0);
        Mat bin = new Mat(); Imgproc.adaptiveThreshold(gray, bin, 255, Imgproc.ADAPTIVE_THRESH_GAUSSIAN_C, Imgproc.THRESH_BINARY, 15, 8);

        string res = "";
        if (ocr != null)
        {
            try { res = ocr.run(bin, 0); }
            catch (Exception e) { Debug.LogWarning("[OCR] Test image OCR falhou: " + e.Message); }
        }
        else
        {
            Debug.LogWarning("[OCR] OCR nativo indisponível para teste.");
        }

        Debug.Log("[OCR Test Image] Resultado: '" + res + "'");
        SaveDebugImage(bin, "test_image");
        bin.Dispose(); gray.Dispose(); rgba.Dispose();
        yield break;
    }
}
