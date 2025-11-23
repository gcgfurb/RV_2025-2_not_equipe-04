using OpenCVForUnity.Calib3dModule;
using OpenCVForUnity.CoreModule;
using OpenCVForUnity.ImgprocModule;
using OpenCVForUnity.UnityIntegration;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using static OpenCVForUnity.UnityIntegration.OpenCVARUtils;

public static class MarkerProcessingUtils
{
    /// <summary>
    /// Processa a lista de cantos e IDs do OpenCV e os transforma em uma lista
    /// de objetos 'DetectedMarker' com pose 3D calculada de forma robusta.
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

                // 2. Calcula o centro 2D (para ordenação visual esquerda->direita)
                Point[] points = imagePoints.toArray();
                double centerX = (points[0].x + points[1].x + points[2].x + points[3].x) / 4;
                double centerY = (points[0].y + points[1].y + points[2].y + points[3].y) / 4;

                PoseData poseData = OpenCVARUtils.ConvertRvecTvecToPoseData(rvec, tvec);

                Vector3 unityPos = poseData.Pos;
                Quaternion unityRot = poseData.Rot;

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

    public static List<ArUcoWordManager.DetectedMarker> FindLargestCluster(List<ArUcoWordManager.DetectedMarker> markers, float maxDist)
    {
        if (markers.Count == 0) return new List<ArUcoWordManager.DetectedMarker>();

        float maxDistSqr = maxDist * maxDist;
        List<ArUcoWordManager.DetectedMarker> largestCluster = new List<ArUcoWordManager.DetectedMarker>();
        HashSet<int> visitedIndexes = new HashSet<int>();

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

                for (int j = 0; j < markers.Count; j++)
                {
                    if (visitedIndexes.Contains(j)) continue;
                    float distSqr = Vector3.SqrMagnitude(currentMarker.unityCameraSpacePosition - markers[j].unityCameraSpacePosition);

                    if (distSqr <= maxDistSqr)
                    {
                        visitedIndexes.Add(j);
                        toVisitIndexes.Enqueue(j);
                    }
                }
            }

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

        result.SortedMarkers = cluster.OrderBy(m => m.center.x).ToList();

        Vector3 averagePos = Vector3.zero;
        string correctWordString = correctWordData.word;

        for (int i = 0; i < result.SortedMarkers.Count; i++)
        {
            ArUcoWordManager.DetectedMarker marker = result.SortedMarkers[i];

            string detectedLetter = codesDictionary.ContainsKey(marker.id) ? codesDictionary[marker.id] : "?";
            result.FormedWord += detectedLetter;

            bool isLetterCorrect = (i < correctWordString.Length && detectedLetter == correctWordString[i].ToString());
            result.LetterCorrectness.Add(isLetterCorrect);

            averagePos += marker.unityCameraSpacePosition;
        }

        result.IsWordCorrect = (result.FormedWord == correctWordString);
        result.AveragePosition_CamSpace = averagePos / result.SortedMarkers.Count;

        // Usa a rotação do marcador central (ou o primeiro) como âncora
        result.AnchorRotation_CamSpace = result.SortedMarkers[result.SortedMarkers.Count / 2].unityRotation;

        return result;
    }

    public static void DrawMarkerFeedback(Mat rgbaMat, List<Mat> allCorners, List<ArUcoWordManager.DetectedMarker> sortedMarkers, List<bool> letterCorrectness, Scalar colorCorrect, Scalar colorWrong)
    {
        if (sortedMarkers.Count != letterCorrectness.Count) return;

        for (int i = 0; i < sortedMarkers.Count; i++)
        {
            // Pega o índice original do marcador para encontrar seus cantos
            int originalIndex = sortedMarkers[i].originalCornersIndex;
            if (originalIndex >= allCorners.Count) continue;
            
            // Decide a cor
            Scalar drawColor = letterCorrectness[i] ? colorCorrect : colorWrong;

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