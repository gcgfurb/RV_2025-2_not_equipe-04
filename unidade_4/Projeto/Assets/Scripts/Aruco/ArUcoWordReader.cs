using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using System.Linq;
using OpenCVForUnity.CoreModule;
using OpenCVForUnity.ObjdetectModule;
using OpenCVForUnity.ImgprocModule;
using OpenCVForUnity.UnityIntegration;
using OpenCVForUnity.UnityIntegration.Helper.Source2Mat;
using TMPro;

#if !OPENCV_DONT_USE_WEBCAMTEXTURE_API
#endif

[RequireComponent(typeof(MultiSource2MatHelper))]
[RequireComponent(typeof(AudioSource))]
public class ArUcoWordManager : MonoBehaviour
{
    public struct DetectedMarker
    {
        public int id;
        public Point center;
        public Vector3 unityCameraSpacePosition;
        public Quaternion unityRotation;
        public int originalCornersIndex;
    }

    [Header("Banco de Palavras (ScriptableObjects)")]
    [Tooltip("Arraste todos os seus assets 'WordData' para esta lista")]
    public List<WordData> allWordsDatabase;

    [Header("Configuração da Palavra (Letras)")]
    public List<MarkerCode> markerCodes;

    [Header("Configuração de Detecção")]
    public ArUcoDictionary dictionaryName = ArUcoDictionary.DICT_6X6_50;
    public float markerLengthMeters = 0.055f;

    [Header("Configuração de Agrupamento")]
    [Tooltip("Distância máxima (em metros) entre dois marcadores para serem parte do mesmo grupo.")]
    public float maxMarkerDistance = 0.1f;

    // --- NOVO: CONFIGURAÇÕES DE ESTABILIDADE E POSICIONAMENTO ---
    [Header("Estabilidade e Posição (Correções)")]
    [Tooltip("Faz o objeto olhar sempre para a câmera (ignora rotação do papel). Ajuda muito na estabilidade.")]
    public bool faceCamera = true;

    [Header("Estabilidade e Posição")]
    [Tooltip("Se ATIVADO: O animal fica sempre em pé (alinhado com a gravidade), mesmo se inclinar o papel. Se DESATIVADO: O animal cola no papel (vira junto com ele).")]
    public bool useGravityAlignment = true;

    [Tooltip("Suavização do movimento. 0.1 a 0.3 é o ideal.")]
    [Range(0.01f, 1.0f)]
    public float smoothingFactor = 0.2f;

    [Tooltip("Ajuste a Rotação Base (X, Y, Z). Use isso para girar o boneco para ficar de frente para onde você quer.")]
    public Vector3 rotationOffset = Vector3.zero;

    [Tooltip("Ajuste a Posição Base (X, Y, Z).")]
    public Vector3 positionOffset = Vector3.zero;

    // Variáveis internas de suavização
    private Vector3 smoothedPosition;
    private Quaternion smoothedRotation;
    private bool hasInitialPose = false;
    // -----------------------------------------------------------

    [Header("Links da UI")]
    [Tooltip("Arraste o Texto da UI para a dica (ex: 'B _ _ _')")]
    public TextMeshProUGUI wordHintText;
    [Tooltip("Arraste o Texto da UI para a palavra formada (ex: 'BAO')")]
    public TextMeshProUGUI wordOutputText;
    [Tooltip("Arraste o Texto da UI para as silabras da palavra formada (ex: 'GA-TO')")]
    public TextMeshProUGUI silabasOutputText;
    [Tooltip("Arraste a RawImage que exibe a câmera")]
    public RawImage outputRawImage;
    public bool displayProcessedImage = true;

    [Header("UI do Som")]
    public Button playSoundButton;

    [Header("UI de Progresso")]
    public Button nextWordButton;

    [Header("Efeitos Visuais")]
    public GameObject confettiPrefab;

    [Header("Debug Props")]
    public List<WordData> debugWordsDatabase;

    // --- Variáveis de Jogo ---
    private WordData correctWordData;
    private WordData activeWordData = null;
    private Dificuldade currentDifficulty;

    private bool isGameActive = false;

    // --- Variáveis Internas do OpenCV ---
    private MultiSource2MatHelper source2MatHelper;
    private Mat rgbaMat, grayMat, camMatrix;
    private MatOfDouble distCoeffs;
    private Texture2D outputTexture;
    private Dictionary dictionary;
    private ArucoDetector detector;
    private DetectorParameters detectorParameters;
    private RefineParameters refineParameters;
    private List<Mat> corners, rejectedCorners;
    private Mat ids;
    private MatOfPoint3f objectPoints;
    private bool hasCelebrated = false;

    // --- Dicionários de Gerenciamento ---
    private Dictionary<int, string> codesDictionary;
    private Dictionary<string, WordData> wordDataDictionary;
    private Dictionary<string, GameObject> instantiatedWordObjects;
    private List<WordData> remainingWords;

    private Camera mainCamera;
    private AudioSource audioSource;

    private readonly Scalar colorCorrect = new(0, 255, 0, 255); // Verde
    private readonly Scalar colorWrong = new(255, 0, 0, 255);   // Vermelho




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
        // Se GameManager existir usa ele, senão usa padrão
        if (GameManager.Instance != null)
            currentDifficulty = GameManager.Instance.dificuldadeAtual;
        else
            currentDifficulty = Dificuldade.Facil;

        mainCamera = Camera.main;
        source2MatHelper = gameObject.GetComponent<MultiSource2MatHelper>();
        source2MatHelper.OutputColorFormat = Source2MatHelperColorFormat.RGBA;
        source2MatHelper.Initialize();

        audioSource = GetComponent<AudioSource>();

        if (debugWordsDatabase != null && debugWordsDatabase.Count > 0)
        {
            allWordsDatabase = debugWordsDatabase;
            Debug.LogWarning("Banco de palavras de depuração ativo!");
        }

        // Popula os dicionários para acesso rápido
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

        instantiatedWordObjects = new Dictionary<string, GameObject>();

        if (playSoundButton != null) playSoundButton.interactable = false;

        StartGame();

    }

    #region Funções Públicas

    public void StartGame()
    {
        Debug.Log($"Iniciando jogo com dificuldade: {currentDifficulty}");
        isGameActive = true;

        remainingWords = allWordsDatabase
            .Where(w => w != null && w.dificuldade == currentDifficulty)
            .OrderBy(x => Random.value)   // embaralhar
            .ToList();

        SortNewWord(); // Sorteia a primeira palavra
    }

    public void StopGame()
    {
        Debug.Log("Parando o jogo.");
        isGameActive = false;
        correctWordData = null;

        if (wordHintText != null) wordHintText.text = "";
        if (wordOutputText != null) wordOutputText.text = "";
        if (silabasOutputText != null) silabasOutputText.text = "";
        if (playSoundButton != null) playSoundButton.interactable = false;

        // Cria um resultado vazio para esconder todos os objetos
        ManageWordObject(new WordAnalysisResult { IsWordCorrect = false });
    }

    public void PlayCurrentWordSound()
    {
        if (activeWordData != null && activeWordData.somDoAnimal != null)
        {
            if (!audioSource.isPlaying)
            {
                audioSource.PlayOneShot(activeWordData.somDoAnimal);
            }
        }
    }

    public void OnNextWordPressed()
    {
        SortNewWord();
    }

    #endregion

    private void SortNewWord()
    {
        if (remainingWords == null || remainingWords.Count == 0)
        {
            // Recomeça o ciclo
            remainingWords = allWordsDatabase
                .Where(w => w != null && w.dificuldade == currentDifficulty)
                .OrderBy(x => Random.value)
                .ToList();
        }

        if (remainingWords.Count > 0)
        {
            correctWordData = remainingWords[0];
            remainingWords.RemoveAt(0);
        }
        else
        {
            Debug.LogError("Nenhuma palavra encontrada no banco de dados.");
            return;
        }

        Debug.Log($"--- NOVA PALAVRA --- {correctWordData.word}");

        // Atualiza dica
        if (wordHintText != null)
        {
            wordHintText.text = $"{correctWordData.word[0]}";
            for (int i = 1; i < correctWordData.word.Length; i++)
                wordHintText.text += " _";
        }

        if (silabasOutputText != null)
            silabasOutputText.text = "";

        if (playSoundButton != null)
            playSoundButton.interactable = false;

        if (nextWordButton != null)
            nextWordButton.interactable = false;

        hasCelebrated = false;
        hasInitialPose = false; // Reseta a suavização para a nova palavra

        // Limpa visualização anterior
        ManageWordObject(new WordAnalysisResult { IsWordCorrect = false });
    }

    // OnSourceToMatHelperInitialized
    public void OnSourceToMatHelperInitialized()
    {
        rgbaMat = source2MatHelper.GetMat();
        outputTexture = new Texture2D(rgbaMat.cols(), rgbaMat.rows(), TextureFormat.RGBA32, false);
        grayMat = new Mat(rgbaMat.rows(), rgbaMat.cols(), CvType.CV_8UC1);

        if (displayProcessedImage && outputRawImage != null)
        {
            outputRawImage.texture = outputTexture;
        }

        // Configuração da Câmera (Dummy)
        int max_d = (int)Mathf.Max(rgbaMat.width(), rgbaMat.height());
        double fx = max_d, fy = max_d;
        double cx = rgbaMat.width() / 2.0f, cy = rgbaMat.height() / 2.0f;
        camMatrix = new Mat(3, 3, CvType.CV_64FC1);
        camMatrix.put(0, 0, fx); camMatrix.put(0, 1, 0); camMatrix.put(0, 2, cx);
        camMatrix.put(1, 0, 0); camMatrix.put(1, 1, fy); camMatrix.put(1, 2, cy);
        camMatrix.put(2, 0, 0); camMatrix.put(2, 1, 0); camMatrix.put(2, 2, 1.0f);
        distCoeffs = new MatOfDouble(0, 0, 0, 0);

        if (markerLengthMeters <= 0) markerLengthMeters = 0.1f;

        // Configuração dos Pontos 3D do Marcador
        float halfMarkerLength = markerLengthMeters / 2.0f;
        objectPoints = new MatOfPoint3f(
            new Point3(-halfMarkerLength, halfMarkerLength, 0),
            new Point3(halfMarkerLength, halfMarkerLength, 0),
            new Point3(halfMarkerLength, -halfMarkerLength, 0),
            new Point3(-halfMarkerLength, -halfMarkerLength, 0)
        );

        // Configuração do Detector ArUco
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
        ids = new Mat();
        rejectedCorners = new List<Mat>();

#if !OPENCV_DONT_USE_WEBCAMTEXTURE_API
        if (source2MatHelper.Source2MatHelper is WebCamTexture2MatHelper webCamHelper)
        {
            if (webCamHelper.IsFrontFacing())
            {
                webCamHelper.FlipHorizontal = true;
            }
        }
#endif
    }

    void Update()
    {
        if (source2MatHelper.IsPlaying() && source2MatHelper.DidUpdateThisFrame())
        {
            rgbaMat = source2MatHelper.GetMat();

            if (!isGameActive || correctWordData == null || markerLengthMeters <= 0)
            {
                if (!isGameActive)
                {
                    ManageWordObject(new WordAnalysisResult { IsWordCorrect = false });
                    if (wordOutputText != null) wordOutputText.text = "";
                }

                if (displayProcessedImage)
                {
                    OpenCVMatUtils.MatToTexture2D(rgbaMat, outputTexture);
                }
                return;
            }

            // Processamento OpenCV
            Imgproc.cvtColor(rgbaMat, grayMat, Imgproc.COLOR_RGBA2GRAY);
            detector.detectMarkers(grayMat, corners, ids, rejectedCorners);

            List<DetectedMarker> allMarkers = MarkerProcessingUtils.ProcessDetectedMarkers(
                corners, ids, objectPoints, camMatrix, distCoeffs
            );

            List<DetectedMarker> mainCluster = MarkerProcessingUtils.FindLargestCluster(allMarkers, maxMarkerDistance);

            WordAnalysisResult analysisResult = MarkerProcessingUtils.AnalyzeCluster(
                mainCluster, correctWordData, codesDictionary
            );

            MarkerProcessingUtils.DrawMarkerFeedback(
                rgbaMat, corners, analysisResult.SortedMarkers,
                analysisResult.LetterCorrectness, colorCorrect, colorWrong
            );

            // Gerencia Objeto 3D com Suavização e Offset
            ManageWordObject(analysisResult);

            if (wordOutputText != null) wordOutputText.text = analysisResult.FormedWord;

            if (displayProcessedImage) OpenCVMatUtils.MatToTexture2D(rgbaMat, outputTexture);

            CleanupFrameMemory();
        }
    }

    private void CleanupFrameMemory()
    {
        foreach (var item in corners) item.Dispose();
        foreach (var item in rejectedCorners) item.Dispose();
        corners.Clear();
        rejectedCorners.Clear();
    }

    /// <summary>
    /// Gerencia qual objeto 3D deve estar ativo, com base no 'WordAnalysisResult'.
    /// APLICA CORREÇÕES DE POSIÇÃO E ROTAÇÃO AQUI.
    /// </summary>
    private void ManageWordObject(WordAnalysisResult result)
    {
        // Esconde objetos não usados
        foreach (var kvp in instantiatedWordObjects)
        {
            if (kvp.Key != result.FormedWord && kvp.Value != null)
                kvp.Value.SetActive(false);
        }

        if (!result.IsWordCorrect)
        {
            activeWordData = null;
            hasInitialPose = false;
            if (playSoundButton != null) playSoundButton.interactable = false;
            if (silabasOutputText != null) silabasOutputText.text = "";
            return;
        }

        if (wordDataDictionary.TryGetValue(result.FormedWord, out WordData data))
        {
            activeWordData = data;
            if (playSoundButton != null) playSoundButton.interactable = true;
            if (silabasOutputText != null) silabasOutputText.text = string.Join("-", data.silabas);
            if (nextWordButton != null) nextWordButton.interactable = true;

            GameObject prefab = data.modelo3D;
            if (prefab == null) return;

            // --- 1. CÁLCULO DA POSIÇÃO (Suavizada) ---
            // O result.AveragePosition_CamSpace JÁ É o centro do grupo de marcadores (centróide)
            Vector3 targetLocalPos = result.AveragePosition_CamSpace;
            Quaternion targetLocalRot = result.AnchorRotation_CamSpace;

            if (!hasInitialPose)
            {
                smoothedPosition = targetLocalPos;
                smoothedRotation = targetLocalRot;
                hasInitialPose = true;
            }
            else
            {
                float t = Mathf.Clamp01(smoothingFactor);
                smoothedPosition = Vector3.Lerp(smoothedPosition, targetLocalPos, t);
                smoothedRotation = Quaternion.Slerp(smoothedRotation, targetLocalRot, t);
            }

            // Converte para Mundo Unity
            Vector3 worldPos = mainCamera.transform.TransformPoint(smoothedPosition);
            Quaternion markerWorldRot = mainCamera.transform.rotation * smoothedRotation;
            Vector3 worldScale = Vector3.one * markerLengthMeters * data.scale;

            // --- 2. INSTANCIAÇÃO ---
            GameObject instance;
            if (!instantiatedWordObjects.TryGetValue(result.FormedWord, out instance))
            {
                instance = Instantiate(prefab);
                instantiatedWordObjects.Add(result.FormedWord, instance);

                //Desativa NavMeshAgent 
                var agent = instance.GetComponent<UnityEngine.AI.NavMeshAgent>();
                if (agent != null) agent.enabled = false;

                //Desativa CharacterController
                var charController = instance.GetComponent<CharacterController>();
                if (charController != null) charController.enabled = false;
            }

            if (instance != null)
            {
                instance.SetActive(true);

                // --- 3. CÁLCULO DA ROTAÇÃO (CORREÇÃO PEDIDA) ---

                Quaternion finalRotation;

                if (useGravityAlignment)
                {
                    // MODO "EM PÉ": O objeto ignora se o papel está inclinado para frente/trás.
                    // Ele usa a direção "Para Frente" do marcador, mas projeta no chão (eixo Y zerado).

                    // Pega o vetor "Forward" (frente) e "Up" (cima) do marcador
                    Vector3 markerForward = markerWorldRot * Vector3.forward;
                    Vector3 markerUp = markerWorldRot * Vector3.up;

                    // Dependendo de como o OpenCV detecta, o "Up" do papel pode ser o Forward da Unity.
                    // Vamos projetar a direção do marcador no plano horizontal do mundo.
                    Vector3 projectedForward = Vector3.ProjectOnPlane(markerUp, Vector3.up); // Tente markerUp ou markerForward aqui se ficar virado errado

                    if (projectedForward.sqrMagnitude > 0.001f)
                    {
                        finalRotation = Quaternion.LookRotation(projectedForward, Vector3.up);
                    }
                    else
                    {
                        finalRotation = Quaternion.identity;
                    }

                    // Aplica o Offset manual EM CIMA da rotação alinhada
                    finalRotation = finalRotation * Quaternion.Euler(rotationOffset);
                }
                else
                {
                    // MODO "COLADO": O objeto segue exatamente a rotação do papel.
                    // Se você inclinar o papel, o animal inclina.
                    finalRotation = markerWorldRot * Quaternion.Euler(rotationOffset);
                }

                // Aplica transformações
                instance.transform.rotation = finalRotation;
                instance.transform.position = worldPos + (finalRotation * positionOffset);
                instance.transform.localScale = worldScale;
            }

            // Confetes
            if (!hasCelebrated)
            {
                SpawnConfetti(worldPos);
                hasCelebrated = true;
            }
        }
    }

    private void SpawnConfetti(Vector3 worldPos)
    {
        if (confettiPrefab != null)
        {
            GameObject confetti = Instantiate(
                confettiPrefab,
                worldPos + Vector3.up * 0.4f,
                Quaternion.identity
            );

            var ps = confetti.GetComponentInChildren<ParticleSystem>();
            if (ps != null)
            {
                var main = ps.main;
                main.startLifetime = 1.5f;
                main.startSpeed = 1.0f;
                main.gravityModifier = 0.1f;

                var emission = ps.emission;
                emission.rateOverTime = 10f;
            }

            Destroy(confetti, 3f);
        }
    }

    // --- Métodos de Limpeza ---
    void OnDestroy()
    {
        if (source2MatHelper != null) source2MatHelper.Dispose();
        if (rgbaMat != null) rgbaMat.Dispose();
        if (grayMat != null) grayMat.Dispose();
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
        foreach (var obj in instantiatedWordObjects.Values) { if (obj != null) Destroy(obj); }
    }

    public void OnSourceToMatHelperDisposed()
    {
        Debug.Log("OnSourceToMatHelperDisposed");
        if (rgbaMat != null) rgbaMat.Dispose();
        if (grayMat != null) grayMat.Dispose();
        if (outputTexture != null) Texture2D.Destroy(outputTexture);
    }

    public void OnSourceToMatHelperErrorOccurred(Source2MatHelperErrorCode errorCode, string message)
    {
        Debug.LogError("OnSourceToMatHelperErrorOccurred " + errorCode + ":" + message);
    }
}