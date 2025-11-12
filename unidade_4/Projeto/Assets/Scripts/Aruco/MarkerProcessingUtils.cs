using System.Collections.Generic;
using System.Linq;
using OpenCVForUnity.Calib3dModule;
using OpenCVForUnity.CoreModule;
using OpenCVForUnity.ImgprocModule;
using OpenCVForUnity.UnityIntegration;
using UnityEngine;

/// <summary>
/// Uma classe auxiliar estática que contém toda a lógica de processamento
/// de marcadores ArUco, seguindo os princípios do Clean Code (SRP).
/// Esta classe não armazena estado, ela apenas recebe dados, processa e retorna.
/// </summary>
public static class MarkerProcessingUtils
{
    /// <summary>
    /// Processa a lista de cantos e IDs do OpenCV e os transforma em uma lista
    /// de objetos 'DetectedMarker' com pose 3D calculada.
    /// </summary>
    /// <param name="corners">A lista de 'corners' vinda do detector.</param>
    /// <param name="ids">A matriz de 'ids' vinda do detector.</param>
    /// <param name="objectPoints">Os pontos 3D do modelo do marcador.</param>
    /// <param name="camMatrix">A matriz da câmera.</param>
    /// <param name="distCoeffs">Os coeficientes de distorção.</param>
    /// <returns>Uma lista de todos os marcadores detectados com sua pose.</returns>
    public static List<ArUcoWordManager.DetectedMarker> ProcessDetectedMarkers(
        List<Mat> corners, Mat ids, MatOfPoint3f objectPoints, Mat camMatrix, MatOfDouble distCoeffs)
    {
        var allMarkers = new List<ArUcoWordManager.DetectedMarker>();
        if (ids.total() == 0) return allMarkers;

        for (int i = 0; i < ids.total(); i++)
        {
            // Usamos 'using' para gerenciar a memória dos objetos de pose (rvec/tvec)
            // e para as cópias dos cantos.
            using (Mat rvec = new Mat(3, 1, CvType.CV_64FC1))
            using (Mat tvec = new Mat(3, 1, CvType.CV_64FC1))
            using (Mat corner_4x1 = corners[i].reshape(2, 4))
            using (MatOfPoint2f imagePoints = new MatOfPoint2f(corner_4x1))
            {
                // Calcula a pose 3D
                bool pnpSuccess = Calib3d.solvePnP(objectPoints, imagePoints, camMatrix, distCoeffs, rvec, tvec);
                if (!pnpSuccess) continue;
                
                // Calcula o centro 2D (para ordenação)
                Point[] points = imagePoints.toArray();
                double centerX = (points[0].x + points[1].x + points[2].x + points[3].x) / 4;
                double centerY = (points[0].y + points[1].y + points[2].y + points[3].y) / 4;
                
                // Converte a pose para o formato Unity
                Vector3 unityPos = OpenCVARUtils.ConvertTvecToPos(tvec);
                Quaternion unityRot = OpenCVARUtils.ConvertRvecToRot(rvec);
                
                allMarkers.Add(new ArUcoWordManager.DetectedMarker
                {
                    id = (int)ids.get(i, 0)[0],
                    center = new Point(centerX, centerY),
                    unityCameraSpacePosition = unityPos,
                    unityRotation = unityRot,
                    originalCornersIndex = i 
                });
            }
        }
        return allMarkers;
    }

    /// <summary>
    /// Encontra o maior grupo (cluster) de marcadores com base em uma distância 3D máxima.
    /// </summary>
    /// <param name="markers">Todos os marcadores detectados no frame.</param>
    /// <param name="maxDist">A distância 3D máxima para considerar vizinhos.</param>
    /// <returns>Uma lista contendo apenas os marcadores do maior grupo.</returns>
    public static List<ArUcoWordManager.DetectedMarker> FindLargestCluster(List<ArUcoWordManager.DetectedMarker> markers, float maxDist)
    {
        if (markers.Count == 0) return new List<ArUcoWordManager.DetectedMarker>();

        float maxDistSqr = maxDist * maxDist; // Usar distância ao quadrado é mais rápido
        List<ArUcoWordManager.DetectedMarker> largestCluster = new List<ArUcoWordManager.DetectedMarker>();
        HashSet<int> visitedIndexes = new HashSet<int>();

        // Algoritmo de busca (Breadth-First Search) para encontrar grupos
        for (int i = 0; i < markers.Count; i++)
        {
            if (visitedIndexes.Contains(i)) continue;

            List<ArUcoWordManager.DetectedMarker> currentCluster = new List<ArUcoWordManager.DetectedMarker>();
            Queue<int> toVisitIndexes = new Queue<int>();

            toVisitIndexes.Enqueue(i);
            visitedIndexes.Add(i);

            while (toVisitIndexes.Count > 0)
            {
                int currentIndex = toVisitIndexes.Dequeue();
                ArUcoWordManager.DetectedMarker currentMarker = markers[currentIndex];
                currentCluster.Add(currentMarker);

                // Encontra todos os vizinhos deste marcador
                for(int j = 0; j < markers.Count; j++)
                {
                    if (visitedIndexes.Contains(j)) continue;

                    // Calcula a distância 3D ao quadrado
                    float distSqr = Vector3.SqrMagnitude(currentMarker.unityCameraSpacePosition - markers[j].unityCameraSpacePosition);

                    if (distSqr <= maxDistSqr)
                    {
                        visitedIndexes.Add(j);
                        toVisitIndexes.Enqueue(j);
                    }
                }
            }
            
            // Ao final da busca, compara o tamanho do grupo encontrado
            if (currentCluster.Count > largestCluster.Count)
            {
                largestCluster = currentCluster;
            }
        }
        return largestCluster;
    }
    
    /// <summary>
    /// Analisa um grupo de marcadores para formar uma palavra e verificar sua correção.
    /// </summary>
    /// <param name="cluster">O grupo de marcadores (já filtrado).</param>
    /// <param name="correctWordData">O ScriptableObject da palavra correta.</param>
    /// <param name="codesDictionary">O dicionário de IDs para letras.</param>
    /// <returns>Um 'WordAnalysisResult' com a palavra formada, status de correção, etc.</returns>
    public static WordAnalysisResult AnalyzeCluster(List<ArUcoWordManager.DetectedMarker> cluster, WordData correctWordData, Dictionary<int, string> codesDictionary)
    {
        WordAnalysisResult result = new WordAnalysisResult
        {
            FormedWord = "",
            IsWordCorrect = false,
            SortedMarkers = new List<ArUcoWordManager.DetectedMarker>(),
            LetterCorrectness = new List<bool>(),
            AveragePosition_CamSpace = Vector3.zero,
            AnchorRotation_CamSpace = Quaternion.identity
        };
        
        if (cluster.Count == 0) return result;

        // Ordena os marcadores da esquerda para a direita (baseado no eixo X 2D)
        result.SortedMarkers = cluster.OrderBy(m => m.center.x).ToList();

        Vector3 averagePos = Vector3.zero;
        string correctWordString = correctWordData.word;

        // Constrói a palavra e a lista de correção
        for(int i = 0; i < result.SortedMarkers.Count; i++)
        {
            ArUcoWordManager.DetectedMarker marker = result.SortedMarkers[i];
            
            // 1. Constrói a palavra
            string detectedLetter = codesDictionary.ContainsKey(marker.id) ? codesDictionary[marker.id] : "?";
            result.FormedWord += detectedLetter;
            
            // 2. Verifica a correção da letra
            bool isLetterCorrect = (i < correctWordString.Length && 
                                    detectedLetter == correctWordString[i].ToString());
            result.LetterCorrectness.Add(isLetterCorrect);
            
            // 3. Soma a Posição para calcular a média
            averagePos += marker.unityCameraSpacePosition;
        }
        
        // Finaliza os cálculos
        result.IsWordCorrect = (result.FormedWord == correctWordString);
        result.AveragePosition_CamSpace = averagePos / result.SortedMarkers.Count;
        result.AnchorRotation_CamSpace = result.SortedMarkers.First().unityRotation;

        return result;
    }
    
    /// <summary>
    /// Desenha o feedback visual (contornos verde/vermelho) na imagem da câmera.
    /// </summary>
    /// <param name="rgbaMat">A Matriz da câmera onde o desenho será feito.</param>
    /// <param name="allCorners">A lista 'corners' original do OpenCV.</param>
    /// <param name="sortedMarkers">A lista de marcadores ordenados (do 'WordAnalysisResult').</param>
    /// <param name="letterCorrectness">A lista de bools (do 'WordAnalysisResult').</param>
    /// <param name="colorCorrect">A cor para 'correto'.</param>
    /// <param name="colorWrong">A cor para 'errado'.</param>
    public static void DrawMarkerFeedback(Mat rgbaMat, List<Mat> allCorners, List<ArUcoWordManager.DetectedMarker> sortedMarkers, List<bool> letterCorrectness, Scalar colorCorrect, Scalar colorWrong)
    {
        if (sortedMarkers.Count != letterCorrectness.Count) return;

        for(int i = 0; i < sortedMarkers.Count; i++)
        {
            // Pega o índice original do marcador para encontrar seus cantos
            int originalIndex = sortedMarkers[i].originalCornersIndex;
            if (originalIndex >= allCorners.Count) continue;
            
            // Decide a cor
            Scalar drawColor = letterCorrectness[i] ? colorCorrect : colorWrong;

            // Desenha o contorno
            // Usamos 'using' para garantir que as cópias de Mat sejam liberadas
            using (Mat markerCornersMat = allCorners[originalIndex]) 
            using (MatOfPoint2f polyCorners = new MatOfPoint2f(markerCornersMat))
            {
                List<MatOfPoint> contours = new List<MatOfPoint>();
                contours.Add(new MatOfPoint(polyCorners.toArray()));
                Imgproc.polylines(rgbaMat, contours, true, drawColor, 4);
            }
        }
    }
}