using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using System.Linq;
using OpenCVForUnity.CoreModule;
using OpenCVForUnity.ObjdetectModule;
using OpenCVForUnity.ImgprocModule;
using OpenCVForUnity.UnityIntegration;
using OpenCVForUnity.UnityIntegration.Helper.Source2Mat;
using OpenCVForUnity.UnityIntegration.Helper.AR;
using UnityEngine.Events;
using OpenCVForUnity.Calib3dModule;

#if !OPENCV_DONT_USE_WEBCAMTEXTURE_API
#endif

[RequireComponent(typeof(MultiSource2MatHelper))]
[RequireComponent(typeof(AudioSource))]
[RequireComponent(typeof(Renderer))] // Garante que este script esteja em um objeto com Renderer (o Quad)
public class ArUcoWordManager : MonoBehaviour
{
    [Header("Configuração de Jogo")]
    public Dificuldade currentDifficulty = Dificuldade.Facil;
    public Text wordHintText;

    [Header("Banco de Palavras (ScriptableObjects)")]
    public List<WordData> allWordsDatabase;

    [Header("Configuração de Detecção")]
    public ArUcoDictionary dictionaryName = ArUcoDictionary.DICT_6X6_50;
    public float markerLengthMeters = 0.055f;

    [Header("Configuração de Agrupamento")]
    public float maxMarkerDistance = 0.1f;

    [Header("Configuração da Palavra (Letras)")]
    public List<MarkerCode> markerCodes;
    public Text wordOutputText;

    [Header("Configuração de Objetos 3D")]
    [Tooltip("Quantos frames o objeto 3D permanece visível após perder a detecção.")]
    public int graceFrames = 10;

    [Header("Links da UI")]
    public bool displayProcessedImage = true;

    [Header("Eventos")]
    public UnityEvent OnWordCorrected;

    [Header("Links da Cena (Obrigatório)")]
    [Tooltip("Arraste a 'Main Camera' (que tem o ARHelper) para cá.")]
    public ARHelper arHelper;

    // --- Variáveis de Jogo ---
    private WordData correctWordData;
    private WordData activeWordData = null;
    private bool isGameActive = false;
    private int framesSinceWordSeen = 0;

    private int anchorMarkerId = -1;
    private bool isWordCurrentlyConfirmed = false;

    // --- Variáveis Internas do OpenCV ---
    private MultiSource2MatHelper source2MatHelper;
    private Mat rgbaMat, grayMat, camMatrix, rgbMat, undistortedRgbMat;
    private MatOfDouble distCoeffs;
    private Texture2D outputTexture; // Textura para o Quad
    private Dictionary dictionary;
    private ArucoDetector detector;
    private DetectorParameters detectorParameters;
    private RefineParameters refineParameters;
    private List<Mat> corners, rejectedCorners;
    private Mat ids;
    private MatOfPoint3f objectPoints;

    // --- Dicionários de Gerenciamento ---
    private Dictionary<int, string> codesDictionary;
    private Dictionary<string, WordData> wordDataDictionary;
    private Dictionary<string, WordPrefab> instantiatedWordObjects;

    private Camera mainCamera;
    private AudioSource audioSource;
    private Renderer quadRenderer; // O Renderer do nosso Quad

    private readonly Scalar colorCorrect = new(0, 255, 0, 255); // Verde
    private readonly Scalar colorWrong = new(255, 0, 0, 255);   // Vermelho


    // Enum de Dicionários (COMPLETO)
    public enum ArUcoDictionary
    {
        DICT_4X4_50 = Objdetect.DICT_4X4_50,
        DICT_4X4_100 = Objdetect.DICT_4X4_100,
        DICT_4X4_250 = Objdetect.DICT_4X4_250,
        DICT_4X4_1000 = Objdetect.DICT_4X4_1000,
        DICT_5X5_50 = Objdetect.DICT_5X5_50,
        DICT_5X5_100 = Objdetect.DICT_5X5_100,
        DICT_5X5_250 = Objdetect.DICT_5X5_250,
        DICT_5X5_1000 = Objdetect.DICT_5X5_1000,
        DICT_6X6_50 = Objdetect.DICT_6X6_50,
        DICT_6X6_100 = Objdetect.DICT_6X6_100,
        DICT_6X6_250 = Objdetect.DICT_6X6_250,
        DICT_6X6_1000 = Objdetect.DICT_6X6_1000,
        DICT_7X7_50 = Objdetect.DICT_7X7_50,
        DICT_7X7_100 = Objdetect.DICT_7X7_100,
        DICT_7X7_250 = Objdetect.DICT_7X7_250,
        DICT_7X7_1000 = Objdetect.DICT_7X7_1000,
        DICT_ARUCO_ORIGINAL = Objdetect.DICT_ARUCO_ORIGINAL,
    }

    // Start
    void Start()
    {
        mainCamera = Camera.main;
        source2MatHelper = gameObject.GetComponent<MultiSource2MatHelper>();
        source2MatHelper.OutputColorFormat = Source2MatHelperColorFormat.RGBA;
        source2MatHelper.Initialize();

        audioSource = GetComponent<AudioSource>();
        quadRenderer = gameObject.GetComponent<Renderer>(); // Pega o renderer do Quad

        if (arHelper == null)
            Debug.LogError("--- ERRO: 'ARHelper' não está configurado no Inspector! ---");

        codesDictionary = new Dictionary<int, string>();
        foreach (var code in markerCodes)
        {
            if (!codesDictionary.ContainsKey(code.id))
                codesDictionary.Add(code.id, code.letter);
        }

        wordDataDictionary = new Dictionary<string, WordData>();
        foreach (var wordData in allWordsDatabase)
        {
            if (wordData == null) continue;
            if (!wordDataDictionary.ContainsKey(wordData.word))
            {
                wordDataDictionary.Add(wordData.word, wordData);
            }
        }

        instantiatedWordObjects = new Dictionary<string, WordPrefab>();
    }

    #region Funções Públicas (API do Jogo)

    private void StartGame(Dificuldade difficulty)
    {
        Debug.Log($"Iniciando jogo com dificuldade: {difficulty}");
        currentDifficulty = difficulty;
        isGameActive = true;
        SortNewWord();
    }
    public void IniciarJogoFacil() { StartGame(Dificuldade.Facil); }
    public void IniciarJogoMedio() { StartGame(Dificuldade.Medio); }
    public void IniciarJogoDificil() { StartGame(Dificuldade.Dificil); }

    public void StopGame()
    {
        Debug.Log("Parando o jogo.");
        isGameActive = false;
        correctWordData = null;
        if (wordHintText != null) wordHintText.text = "";
        if (wordOutputText != null) wordOutputText.text = "";
        HideAllWordObjects();
    }

    public void PlayCurrentWordSound()
    {
        if (activeWordData != null && activeWordData.somDoAnimal != null)
        {
            if (!audioSource.isPlaying)
                audioSource.PlayOneShot(activeWordData.somDoAnimal);
        }
    }
    #endregion

    private void SortNewWord()
    {
        List<WordData> availableWords = allWordsDatabase.Where(w => w != null && w.dificuldade == currentDifficulty).ToList();
        if (availableWords.Count == 0)
        {
            Debug.LogError($"Nenhuma palavra encontrada para a dificuldade: {currentDifficulty}.");
            correctWordData = null;
            isGameActive = false;
            return;
        }

        correctWordData = availableWords[Random.Range(0, availableWords.Count)];
        Debug.Log($"--- NOVO JOGO --- Palavra Correta: {correctWordData.word}");

        if (wordHintText != null)
        {
            wordHintText.text = $"{correctWordData.word[0]}";
            for (int i = 1; i < correctWordData.word.Length; i++)
                wordHintText.text += " _";
        }

        framesSinceWordSeen = 0;
        isWordCurrentlyConfirmed = false;
        anchorMarkerId = -1;
        HideAllWordObjects();
    }

    public void OnSourceToMatHelperInitialized()
    {
        Debug.Log("OnSourceToMatHelperInitialized");

        // ============
        // 1. MATRIZES
        // ============    
        rgbaMat = source2MatHelper.GetMat();

        rgbMat = new Mat(rgbaMat.rows(), rgbaMat.cols(), CvType.CV_8UC3);
        undistortedRgbMat = new Mat(rgbaMat.rows(), rgbaMat.cols(), CvType.CV_8UC3);
        grayMat = new Mat(rgbaMat.rows(), rgbaMat.cols(), CvType.CV_8UC1);

        // ============
        // 2. TEXTURA DE SAÍDA (QUE APARECE NO QUAD)
        // ============
        outputTexture = new Texture2D(
            rgbaMat.cols(),
            rgbaMat.rows(),
            TextureFormat.RGBA32,
            false
        );

        if (quadRenderer != null)
            quadRenderer.material.mainTexture = outputTexture;


        // ============
        // 3. AJUSTE DO QUAD (COPIADO DO ArUcoExample)
        // ============        
        mainCamera.orthographic = true;
        mainCamera.orthographicSize = rgbaMat.height() / 2f;

        float cameraAspect = mainCamera.aspect;
        float textureAspect = (float)rgbaMat.width() / rgbaMat.height();

        float imageSizeScale;

        if (textureAspect > cameraAspect)
        {
            float cameraWidth = mainCamera.orthographicSize * 2f * cameraAspect;
            imageSizeScale = cameraWidth / rgbaMat.width();
        }
        else
        {
            float cameraHeight = mainCamera.orthographicSize * 2f;
            imageSizeScale = cameraHeight / rgbaMat.height();
        }

        transform.localScale = new Vector3(
            rgbaMat.width() * imageSizeScale,
            rgbaMat.height() * imageSizeScale,
            1f
        );

        Debug.Log("[ARUCO] imageSizeScale = " + imageSizeScale);


        // ============
        // 4. MATRIZ DA CÂMERA (igual ao exemplo)
        // ============
        float w = rgbaMat.width();
        float h = rgbaMat.height();
        int max_d = (int)Mathf.Max(w, h);

        double fx = max_d;
        double fy = max_d;
        double cx = w / 2.0;
        double cy = h / 2.0;

        camMatrix = new Mat(3, 3, CvType.CV_64FC1);
        camMatrix.put(0, 0, fx); camMatrix.put(0, 1, 0); camMatrix.put(0, 2, cx);
        camMatrix.put(1, 0, 0); camMatrix.put(1, 1, fy); camMatrix.put(1, 2, cy);
        camMatrix.put(2, 0, 0); camMatrix.put(2, 1, 0); camMatrix.put(2, 2, 1);

        distCoeffs = new MatOfDouble(0, 0, 0, 0);

        if (markerLengthMeters <= 0)
        {
            Debug.LogWarning("[ARUCO] markerLengthMeters inválido. Ajustado para 0.1");
            markerLengthMeters = 0.1f;
        }


        // ============    
        // 5. CONFIGURA O ARHELPER (IDÊNTICO AO EXEMPLO)
        // ============
        if (arHelper != null && arHelper.ARCamera != null)
        {
            arHelper.Initialize();

            arHelper.ARCamera.SetCamMatrix(camMatrix);
            arHelper.ARCamera.SetDistCoeffs(distCoeffs);

            arHelper.ARCamera.SetARCameraParameters(
                Screen.width,
                Screen.height,
                rgbaMat.width(),
                rgbaMat.height(),
                Vector2.zero,
                new Vector2(imageSizeScale, imageSizeScale)
            );

            Debug.Log("[ARUCO] ARHelper e ARCamera configurados.");
        }
        else
        {
            Debug.LogError("[ARUCO] ARHelper ou ARCamera não configurados no Inspector!");
        }


        // ============    
        // 6. OBJETOS 3D — PONTOS DO MARCADOR
        // ============
        float s = markerLengthMeters / 2f;

        objectPoints = new MatOfPoint3f(
            new Point3(-s, s, 0),
            new Point3(s, s, 0),
            new Point3(s, -s, 0),
            new Point3(-s, -s, 0)
        );


        // ============    
        // 7. DETECTOR ARUCO (EXATO DO EXEMPLO)
        // ============
        dictionary = Objdetect.getPredefinedDictionary((int)dictionaryName);

        detectorParameters = new DetectorParameters();
        detectorParameters.set_minDistanceToBorder(3);
        detectorParameters.set_useAruco3Detection(true);
        detectorParameters.set_cornerRefinementMethod(Objdetect.CORNER_REFINE_SUBPIX);
        detectorParameters.set_minSideLengthCanonicalImg(16);
        detectorParameters.set_errorCorrectionRate(0.8);

        refineParameters = new RefineParameters();

        detector = new ArucoDetector(dictionary, detectorParameters, refineParameters);

        corners = new List<Mat>();
        rejectedCorners = new List<Mat>();
        ids = new Mat();

        Debug.Log("[ARUCO] Detector configurado 100% igual ao exemplo.");


#if !OPENCV_DONT_USE_WEBCAMTEXTURE_API
        if (source2MatHelper.Source2MatHelper is WebCamTexture2MatHelper wc)
        {
            if (wc.IsFrontFacing())
            {
                wc.FlipHorizontal = true;
                Debug.LogWarning("[ARUCO] Webcam frontal detectada — invertendo horizontal.");
            }
        }
#endif
    }

    /// <summary>
    /// Loop principal, executado a cada frame.
    /// </summary>
    void Update()
    {
        if (arHelper == null) return;

        if (source2MatHelper.IsPlaying() && source2MatHelper.DidUpdateThisFrame())
        {
            rgbaMat = source2MatHelper.GetMat();

            // Converte RGBA -> RGB
            Imgproc.cvtColor(rgbaMat, rgbMat, Imgproc.COLOR_RGBA2RGB);

            // AQUI: undistort antes de qualquer detecção/uso
            Calib3d.undistort(rgbMat, undistortedRgbMat, camMatrix, distCoeffs);

            // Converte para grayscale a imagem já corrigida
            Imgproc.cvtColor(undistortedRgbMat, grayMat, Imgproc.COLOR_RGB2GRAY);

            // Detecta marcadores na imagem CORRIGIDA
            detector.detectMarkers(grayMat, corners, ids, rejectedCorners);

            // Reset ARGameObjects ImagePoints and ObjectPoints.
            arHelper.ResetARGameObjectsImagePointsAndObjectPoints();

            string palavraDetectada = "";
            List<DetectedMarkerSimple> detectedMarkers = new List<DetectedMarkerSimple>();
            if (ids.total() > 0)
            {
                for (int i = 0; i < ids.total(); i++)
                {
                    using (Mat corner_4x1 = corners[i].reshape(2, 4))
                    using (MatOfPoint2f imagePoints = new MatOfPoint2f(corner_4x1))
                    {
                        Point[] points = imagePoints.toArray();
                        double centerX = (points[0].x + points[1].x + points[2].x + points[3].x) / 4;
                        detectedMarkers.Add(new DetectedMarkerSimple
                        {
                            id = (int)ids.get(i, 0)[0],
                            center = centerX,
                            index = i
                        });
                    }
                }
                detectedMarkers = detectedMarkers.OrderBy(m => m.center).ToList();

                foreach (var marker in detectedMarkers)
                {
                    if (codesDictionary.ContainsKey(marker.id))
                        palavraDetectada += codesDictionary[marker.id];
                    else
                        palavraDetectada += "?";
                }
            }

            bool isWordNowCorrect = (palavraDetectada == correctWordData?.word);

            if (isWordNowCorrect && !isWordCurrentlyConfirmed)
            {
                isWordCurrentlyConfirmed = true;
                OnWordCorrected.Invoke();
                int middleIndex = detectedMarkers.Count / 2;
                anchorMarkerId = detectedMarkers[middleIndex].id;
                Debug.Log($"PALAVRA CORRETA! Travando no marcador âncora ID: {anchorMarkerId}");
            }

            if (ids.total() > 0)
            {
                if (!isWordCurrentlyConfirmed)
                {
                    framesSinceWordSeen = graceFrames;
                    HideAllWordObjects();

                    for (int i = 0; i < detectedMarkers.Count; i++)
                    {
                        var marker = detectedMarkers[i];
                        string detectedLetter = codesDictionary.ContainsKey(marker.id) ? codesDictionary[marker.id] : "?";
                        bool isLetterCorrect = (i < correctWordData.word.Length &&
                                                detectedLetter == correctWordData.word[i].ToString());

                        Scalar drawColor = isLetterCorrect ? colorCorrect : colorWrong;
                        DrawMarkerContour(undistortedRgbMat, corners[marker.index], drawColor); // Desenha no undistortedRgbMat
                    }
                }
                else
                {
                    bool anchorFound = false;
                    for (int i = 0; i < ids.total(); i++)
                    {
                        int currentId = (int)ids.get(i, 0)[0];

                        DrawMarkerContour(undistortedRgbMat, corners[i], colorCorrect); // Todos verdes

                        if (currentId == anchorMarkerId)
                        {
                            WordPrefab wordObject = GetOrCreateWordObject(correctWordData);
                            if (wordObject != null)
                            {
                                using (Mat corner_4x1 = corners[i].reshape(2, 4))
                                using (MatOfPoint2f imagePoints = new MatOfPoint2f(corner_4x1))
                                {
                                    wordObject.ImagePoints = imagePoints.toVector2Array();
                                }
                                wordObject.ObjectPoints = objectPoints.toVector3Array();

                                wordObject.gameObject.SetActive(true);
                                activeWordData = correctWordData;
                                anchorFound = true;
                                framesSinceWordSeen = 0;
                            }
                        }
                    }

                    if (!anchorFound)
                    {
                        framesSinceWordSeen++;
                        if (framesSinceWordSeen > graceFrames)
                        {
                            HideAllWordObjects();
                        }
                    }
                }
            }
            else
            {
                framesSinceWordSeen++;
                if (framesSinceWordSeen > graceFrames)
                {
                    HideAllWordObjects();
                }
            }

            if (wordOutputText != null)
            {
                wordOutputText.text = palavraDetectada;
            }

            // -----------------------
            // Renderiza a imagem corrigida no Quad
            // -----------------------
            Mat displayMat = new Mat();
            Imgproc.cvtColor(undistortedRgbMat, displayMat, Imgproc.COLOR_RGB2RGBA);
            OpenCVMatUtils.MatToTexture2D(displayMat, outputTexture);
            displayMat.Dispose();

            CleanupFrameMemory();
        }
    }

    /// <summary>
    /// Limpa as Matrizes 'corners' e 'rejectedCorners' no final do frame.
    /// </summary>
    private void CleanupFrameMemory()
    {
        foreach (var item in corners) item.Dispose();
        foreach (var item in rejectedCorners) item.Dispose();
        corners.Clear();
        rejectedCorners.Clear();
    }

    /// <summary>
    /// Desenha o contorno de um marcador na imagem.
    /// </summary>
    private void DrawMarkerContour(Mat imageToDrawOn, Mat markerCorners, Scalar color)
    {
        using (MatOfPoint2f polyCorners = new MatOfPoint2f(markerCorners))
        {
            List<MatOfPoint> contours = new List<MatOfPoint>
            {
                new MatOfPoint(polyCorners.toArray())
            };
            Imgproc.polylines(imageToDrawOn, contours, true, color, 4);
        }
    }

private WordPrefab GetOrCreateWordObject(WordData data)
{
    if (data == null || data.modelo3D == null) 
        return null;

    string currentWord = data.word;

    // Desativa outras palavras
    foreach (var pair in instantiatedWordObjects)
    {
        if (pair.Key != currentWord && pair.Value != null)
            pair.Value.gameObject.SetActive(false);
    }

    if (instantiatedWordObjects.TryGetValue(currentWord, out WordPrefab existing))
    {
        if (existing != null)
        {
            existing.transform.localScale = Vector3.one * markerLengthMeters;
            return existing;
        }
    }

    // ================================
    // CRIAÇÃO DO WORDPREFAB GENÉRICO
    // ================================

    // 1. Anchor principal (pai)
    GameObject anchor = new GameObject(data.word + "_Anchor");
    anchor.transform.SetParent(arHelper.transform, false);

    // 2. Adiciona o WordPrefab (derivado de ARGameObject)
    WordPrefab wp = anchor.AddComponent<WordPrefab>();
    wp.LeftHandedCoordinates = true;
    wp.markerLengthMeters = markerLengthMeters;

    // Define suavização (opcional)
    wp.UseLowPassFilter = true;
    wp.PositionLowPassParam = 0.05f;
    wp.RotationLowPassParam = 5f;

    // ======================================
    // 3. Criar hierarquia interna automática
    // ======================================

    // (A) RotationFix
    GameObject rotFixObj = new GameObject("RotationFix");
    rotFixObj.transform.SetParent(wp.transform, false);

    // (B) ModelRoot
    GameObject modelRootObj = new GameObject("ModelRoot");
    modelRootObj.transform.SetParent(rotFixObj.transform, false);

    // Guardar essas refs no WordPrefab
    wp.rotationFix = rotFixObj.transform;
    wp.modelRoot   = modelRootObj.transform;

    // ======================================
    // 4. Instanciar modelo 3D dentro do ModelRoot
    // ======================================

    GameObject model = Instantiate(data.modelo3D);
    model.transform.SetParent(modelRootObj.transform, false);

    // Rotação padrão OpenCV → Unity
    model.transform.localRotation = Quaternion.Euler(-90, 0, 0);
    model.transform.localPosition = Vector3.zero;
    model.transform.localScale = Vector3.one;

    wp.modelInstance = model.transform;

    // 6. Registrar no ARHelper
    arHelper.ARGameObjects.Add(wp);
    instantiatedWordObjects[currentWord] = wp;

    return wp;
}


    /// <summary>
    /// Esconde todos os objetos 3D gerenciados.
    /// </summary>
    private void HideAllWordObjects()
    {
        if (arHelper != null && arHelper.ARGameObjects != null)
        {
            foreach (var wordPrefab in instantiatedWordObjects.Values)
            {
                if (wordPrefab != null)
                {
                    arHelper.ARGameObjects.Remove(wordPrefab);
                    Destroy(wordPrefab.gameObject);
                }
            }
        }

        instantiatedWordObjects.Clear();
        activeWordData = null;
    }


    // --- Métodos de Limpeza ---
    void OnDestroy()
    {
        if (source2MatHelper != null) source2MatHelper.Dispose();
        if (rgbaMat != null) rgbaMat.Dispose();
        if (grayMat != null) grayMat.Dispose();
        if (rgbMat != null) rgbMat.Dispose();
        if (undistortedRgbMat != null) undistortedRgbMat.Dispose();
        if (outputTexture != null) Texture2D.Destroy(outputTexture);
        if (ids != null) ids.Dispose();
        if (dictionary != null) dictionary.Dispose();
        if (detector != null) detector.Dispose();
        if (refineParameters != null) refineParameters.Dispose();

        if (corners != null) foreach (var mat in corners) mat.Dispose();
        if (rejectedCorners != null) foreach (var mat in rejectedCorners) mat.Dispose();

        if (camMatrix != null) camMatrix.Dispose();
        if (distCoeffs != null) distCoeffs.Dispose();
        if (objectPoints != null) objectPoints.Dispose();

        if (arHelper != null)
        {
            arHelper.Dispose();
        }
        foreach (var obj in instantiatedWordObjects.Values)
        {
            if (obj != null) Destroy(obj.gameObject);
        }
        instantiatedWordObjects.Clear();
    }

    // As funções OnSource... DEvem ser públicas para o UnityEvent
    public void OnSourceToMatHelperDisposed()
    {
        Debug.Log("OnSourceToMatHelperDisposed");
        if (rgbaMat != null) rgbaMat.Dispose();
        if (grayMat != null) grayMat.Dispose();
        if (rgbMat != null) rgbMat.Dispose();
        if (undistortedRgbMat != null) undistortedRgbMat.Dispose();
        if (outputTexture != null) Texture2D.Destroy(outputTexture);
    }
    public void OnSourceToMatHelperErrorOccurred(Source2MatHelperErrorCode errorCode, string message)
    {
        Debug.LogError("OnSourceToMatHelperErrorOccurred " + errorCode + ":" + message);
    }

    // Struct auxiliar para ordenação
    private struct DetectedMarkerSimple
    {
        public int id;
        public double center;
        public int index;
    }
}
