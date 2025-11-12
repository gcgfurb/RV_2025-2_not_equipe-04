using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Uma estrutura de dados (DTO) que armazena os resultados
/// da análise dos marcadores em um único frame.
/// Isso evita passar 10 parâmetros diferentes entre as funções.
/// </summary>
public struct WordAnalysisResult
{
    /// <summary>
    /// A palavra formada pelos marcadores detectados, ex: "BOLA" ou "BOA?".
    /// </summary>
    public string FormedWord;

    /// <summary>
    /// Verdadeiro se a FormedWord for idêntica à palavra correta do jogo.
    /// </summary>
    public bool IsWordCorrect;

    /// <summary>
    /// A lista de marcadores que fazem parte do grupo principal, já ordenados da esquerda para a direita.
    /// </summary>
    public List<ArUcoWordManager.DetectedMarker> SortedMarkers;

    /// <summary>
    /// Uma lista de 'bool' que corresponde a SortedMarkers. 
    /// Indica se a letra na posição [i] está correta. (true = verde, false = vermelho).
    /// </summary>
    public List<bool> LetterCorrectness;

    /// <summary>
    /// A posição 3D média (no espaço da câmera) de todos os marcadores no grupo.
    /// </summary>
    public Vector3 AveragePosition_CamSpace;

    /// <summary>
    /// A rotação 3D (no espaço da câmera) do primeiro marcador do grupo (a âncora).
    /// </summary>
    public Quaternion AnchorRotation_CamSpace;
}