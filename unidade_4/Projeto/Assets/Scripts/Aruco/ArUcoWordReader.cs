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
    // O struct 'DetectedMarker' é público para que
    // a classe auxiliar 'MarkerProcessingUtils' possa usá-lo.
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
    
    [Header("Configuração de Objetos 3D")]
    [Tooltip("Multiplicador para o tamanho do objeto. 1 = tamanho real do marcador.")]
    public float scaleMultiplier = 1.0f;

    [Header("Links da UI")]
    [Tooltip("Arraste o Texto da UI para a dica (ex: 'B _ _ _')")]
    public TextMeshProUGUI wordHintText;
    [Tooltip("Arraste o Texto da UI para a palavra formada (ex: 'BAO')")]
    public TextMeshProUGUI wordOutputText; 
    [Tooltip("Arraste a RawImage que exibe a câmera")]
    public RawImage outputRawImage;
    public bool displayProcessedImage = true;

    [Header("UI do Som")]
    public Button playSoundButton;

    // --- Variáveis de Jogo ---
    private WordData correctWordData; 
    private string lastPlayedWord = ""; 
    private WordData activeWordData = null;
    private Dificuldade currentDifficulty;
    
    // --- MUDANÇA AQUI: Variável de Estado ---
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
    
    // --- Dicionários de Gerenciamento ---
    private Dictionary<int, string> codesDictionary; 
    private Dictionary<string, WordData> wordDataDictionary;
    private Dictionary<string, GameObject> instantiatedWordObjects;
    
    private Camera mainCamera; 
    private AudioSource audioSource;
    
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
        currentDifficulty = GameManager.Instance.dificuldadeAtual;
        mainCamera = Camera.main; 
        source2MatHelper = gameObject.GetComponent<MultiSource2MatHelper>();
        source2MatHelper.OutputColorFormat = Source2MatHelperColorFormat.RGBA;
        source2MatHelper.Initialize();
        
        audioSource = GetComponent<AudioSource>();

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

    /// <summary>
    /// Esta é a função principal para iniciar o jogo.
    /// Chame isso a partir de um botão de UI (ex: "Fácil", "Médio").
    /// </summary>
    public void StartGame()
    {
        Debug.Log($"Iniciando jogo com dificuldade: {currentDifficulty}");
        isGameActive = true;
        SortNewWord(); // Sorteia a primeira palavra
    }

    /// <summary>
    /// Para o jogo, limpa a UI e esconde os objetos.
    /// Chame isso a partir de um botão "Voltar ao Menu".
    /// </summary>
    public void StopGame()
    {
        Debug.Log("Parando o jogo.");
        isGameActive = false;
        correctWordData = null;
        
        if(wordHintText != null) wordHintText.text = "";
        if(wordOutputText != null) wordOutputText.text = "";
        if (playSoundButton != null) playSoundButton.interactable = false;

        // Cria um resultado vazio para esconder todos os objetos
        ManageWordObject(new WordAnalysisResult { IsWordCorrect = false });
    }
    
    /// <summary>
    /// Função pública para o botão da UI tocar o som do animal ativo.
    /// </summary>
    public void PlayCurrentWordSound()
    {
        Debug.LogWarning("Botão de som clicado, mas nenhum animal está 321312312312 m,icaxl.");
        if (activeWordData != null && activeWordData.somDoAnimal != null)
        {
            Debug.LogWarning("Botão de som clicado, mas nenhum animal está ativvivivivio.");
            if (!audioSource.isPlaying)
            {
                Debug.Log($"Tocando som (via botão) para: {activeWordData.word}");
                audioSource.PlayOneShot(activeWordData.somDoAnimal);
            }
        }
        else
        {
            Debug.LogWarning("Botão de som clicado, mas nenhum animal está ativo.");
        }
    }

    #endregion

    /// <summary>
    /// Sorteia uma nova palavra com base na dificuldade atual.
    /// </summary>
    private void SortNewWord() // Renomeado de StartNewGame para clareza
    {
        List<WordData> availableWords = allWordsDatabase.Where(w => w != null && w.dificuldade == currentDifficulty).ToList();

        if (availableWords.Count == 0)
        {
            Debug.LogError($"Nenhuma palavra encontrada para a dificuldade: {currentDifficulty}.");
            correctWordData = null;
            isGameActive = false; // Para o jogo se não houver palavras
            return;
        }

        correctWordData = availableWords[Random.Range(0, availableWords.Count)];
        Debug.Log($"--- NOVO JOGO --- Palavra Correta: {correctWordData.word}");

        if (wordHintText != null)
        {
            wordHintText.text = $"{correctWordData.word[0]}";
            for (int i = 1; i < correctWordData.word.Length; i++)
            {
                wordHintText.text += " _";
            }
        }

        if (playSoundButton != null) playSoundButton.interactable = false;
        lastPlayedWord = "";
        ManageWordObject(new WordAnalysisResult { IsWordCorrect = false });
    }
    
    // OnSourceToMatHelperInitialized
    public void OnSourceToMatHelperInitialized()
    {
        Debug.Log("OnSourceToMatHelperInitialized");
        rgbaMat = source2MatHelper.GetMat();
        outputTexture = new Texture2D(rgbaMat.cols(), rgbaMat.rows(), TextureFormat.RGBA32, false);
        grayMat = new Mat(rgbaMat.rows(), rgbaMat.cols(), CvType.CV_8UC1);

        if (displayProcessedImage && outputRawImage != null)
        {
            outputRawImage.texture = outputTexture;
            //outputRawImage.rectTransform.sizeDelta = new Vector2(rgbaMat.cols(), rgbaMat.rows());
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

        if (markerLengthMeters <= 0)
        {
            Debug.LogError($"[ERRO FATAL] 'Marker Length Meters' é {markerLengthMeters}.");
            markerLengthMeters = 0.1f; 
        }
        Debug.Log($"[LOG INICIALIZAÇÃO] Usando Marker Length de {markerLengthMeters} metros.");
        
        // Configuração dos Pontos 3D do Marcador (modelo)
        float halfMarkerLength = markerLengthMeters / 2.0f;
        objectPoints = new MatOfPoint3f(
            new Point3(-halfMarkerLength,  halfMarkerLength, 0),
            new Point3( halfMarkerLength,  halfMarkerLength, 0),
            new Point3( halfMarkerLength, -halfMarkerLength, 0),
            new Point3(-halfMarkerLength, -halfMarkerLength, 0)
        );

        // Configuração do Detector ArUco (com parâmetros robustos)
        dictionary = Objdetect.getPredefinedDictionary((int)dictionaryName);
        detectorParameters = new DetectorParameters(); 
        detectorParameters.set_minDistanceToBorder(3);
        detectorParameters.set_useAruco3Detection(true); 
        detectorParameters.set_cornerRefinementMethod(Objdetect.CORNER_REFINE_SUBPIX);
        detectorParameters.set_minSideLengthCanonicalImg(16);
        detectorParameters.set_errorCorrectionRate(0.8);
        refineParameters = new RefineParameters();
        detector = new ArucoDetector(dictionary, detectorParameters, refineParameters);

        // Inicializa listas de detecção do OpenCV
        corners = new List<Mat>();
        ids = new Mat();
        rejectedCorners = new List<Mat>();

        Debug.Log("Detector ArUco (v3.0.0) com Estimativa de Pose pronto.");

        // Correção de Espelhamento
        #if !OPENCV_DONT_USE_WEBCAMTEXTURE_API
        if (source2MatHelper.Source2MatHelper is WebCamTexture2MatHelper webCamHelper)
        {
             if (webCamHelper.IsFrontFacing())
            {
                webCamHelper.FlipHorizontal = true; 
                Debug.LogWarning("--- WEBCAM FRONTAL DETECTADA --- Ativando 'FlipHorizontal' para corrigir espelhamento.");
            }
        }
        #endif
    }

    /// <summary>
    /// Loop principal, executado a cada frame.
    /// Orquestra a detecção, processamento e apresentação.
    /// </summary>
    void Update()
    {
        if (source2MatHelper.IsPlaying() && source2MatHelper.DidUpdateThisFrame())
        {
            // 1. Pega o frame da câmera
            rgbaMat = source2MatHelper.GetMat();

            // 2. VERIFICA SE O JOGO ESTÁ ATIVO
            if (!isGameActive || correctWordData == null || markerLengthMeters <= 0) 
            {
                // Se o jogo não estiver ativo, limpa os desenhos (caso haja lixo)
                if(!isGameActive)
                {
                    ManageWordObject(new WordAnalysisResult { IsWordCorrect = false });
                    if(wordOutputText != null) wordOutputText.text = "";
                }
                
                // Apenas exibe a imagem "crua" da câmera e sai.
                if (displayProcessedImage)
                {
                    OpenCVMatUtils.MatToTexture2D(rgbaMat, outputTexture);
                }
                return; // Sai do Update
            }
            
            // --- O JOGO ESTÁ ATIVO, EXECUTA A LÓGICA COMPLETA ---
            
            // 3. Processa a imagem (converte para cinza e detecta)
            Imgproc.cvtColor(rgbaMat, grayMat, Imgproc.COLOR_RGBA2GRAY);
            detector.detectMarkers(grayMat, corners, ids, rejectedCorners);
            
            // 4. Converte os dados brutos do OpenCV em nossa lista de marcadores com pose 3D
            List<DetectedMarker> allMarkers = MarkerProcessingUtils.ProcessDetectedMarkers(
                corners, ids, objectPoints, camMatrix, distCoeffs
            );
            
            // 5. Filtra os marcadores para encontrar o maior grupo
            List<DetectedMarker> mainCluster = MarkerProcessingUtils.FindLargestCluster(allMarkers, maxMarkerDistance);

            // 6. Analisa o grupo para formar a palavra e verificar se está correta
            WordAnalysisResult analysisResult = MarkerProcessingUtils.AnalyzeCluster(
                mainCluster, correctWordData, codesDictionary
            );
            
            // 7. Desenha o feedback (verde/vermelho) na imagem
            MarkerProcessingUtils.DrawMarkerFeedback(
                rgbaMat, corners, analysisResult.SortedMarkers, 
                analysisResult.LetterCorrectness, colorCorrect, colorWrong
            );

            // 8. Gerencia o objeto 3D (mostra/esconde) e toca o som
            ManageWordObject(analysisResult);
            
            // 9. Atualiza os textos da UI
            if (wordOutputText != null)
            {
                wordOutputText.text = analysisResult.FormedWord;
            }

            // 10. Exibe a imagem processada na tela
            if (displayProcessedImage)
            {
                OpenCVMatUtils.MatToTexture2D(rgbaMat, outputTexture);
            }

            // 11. Limpa a memória das Matrizes do OpenCV para o próximo frame
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
    /// Gerencia qual objeto 3D deve estar ativo, com base no 'WordAnalysisResult'.
    /// </summary>
    private void ManageWordObject(WordAnalysisResult result)
    {
        // Esconde todos os objetos primeiro
        foreach (var obj in instantiatedWordObjects.Values) 
        { 
            if (obj != null) obj.SetActive(false); 
        }
        
        // Se a palavra não estiver correta ou não houver palavra, sai
        if (!result.IsWordCorrect) 
        {
            lastPlayedWord = ""; // Reseta o som
            activeWordData = null;
            if (playSoundButton != null) playSoundButton.interactable = false;
            return;
        }

        // Tenta encontrar a palavra no banco de dados
        if (wordDataDictionary.TryGetValue(result.FormedWord, out WordData data)) 
        {
            activeWordData = data; // Define o animal ativo
            if (playSoundButton != null) playSoundButton.interactable = true;
            GameObject prefab = data.modelo3D; 
            if (prefab == null) return;

            // Converte a pose média para o Espaço do Mundo
            Vector3 worldPos = mainCamera.transform.TransformPoint(result.AveragePosition_CamSpace);
            Quaternion worldRot = mainCamera.transform.rotation * result.AnchorRotation_CamSpace;
            Vector3 worldScale = Vector3.one * markerLengthMeters * scaleMultiplier;

            GameObject instance;
            if (instantiatedWordObjects.TryGetValue(result.FormedWord, out instance))
            {
                if (instance != null)
                {
                    instance.SetActive(true);
                    instance.transform.position = worldPos;
                    instance.transform.localScale = worldScale;
                }
                else
                {
                    instance = Instantiate(prefab, worldPos, Quaternion.identity);
                    instance.transform.localScale = worldScale;
                    instantiatedWordObjects[result.FormedWord] = instance; 
                }
            }
            else
            {
                instance = Instantiate(prefab, worldPos, Quaternion.identity);
                instance.transform.localScale = worldScale;
                instantiatedWordObjects.Add(result.FormedWord, instance);
            }

            // Aplica a rotação "em pé"
            Vector3 markerForward = worldRot * Vector3.forward;
            Quaternion standingRotation = Quaternion.LookRotation(markerForward, Vector3.up);
            instance.transform.rotation = standingRotation;
        }
        else
        {
            activeWordData = null; // Palavra formada não está no banco de dados
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

    // --- FUNÇÕES DE ATALHO PARA OS BOTÕES ---

    /// <summary>
    /// Atalho para o botão "Fácil".
    /// </summary>
    //public void IniciarJogoFacil()
    //{
    //    StartGame(Dificuldade.Facil);
    //}

    ///// <summary>
    ///// Atalho para o botão "Médio".
    ///// </summary>
    //public void IniciarJogoMedio()
    //{
    //    StartGame(Dificuldade.Medio);
    //}

    ///// <summary>
    ///// Atalho para o botão "Difícil".
    ///// </summary>
    //public void IniciarJogoDificil()
    //{
    //    StartGame(Dificuldade.Dificil);
    //}
}