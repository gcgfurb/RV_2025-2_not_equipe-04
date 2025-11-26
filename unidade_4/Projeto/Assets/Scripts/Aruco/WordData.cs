using UnityEngine;

// Coloque esta classe em seu próprio arquivo "WordData.cs"
[CreateAssetMenu(fileName = "NovaPalavra", menuName = "Palavras/WordData")]
public class WordData : ScriptableObject
{
    public string word;
    public string[] silabas;
    public Categoria categoria;
    public AudioClip somDoAnimal; // O nome pode ser genérico, como 'somDaPalavra'
    public GameObject modelo3D;
    public Dificuldade dificuldade;

    [Tooltip("Escala individual do modelo 3D (1 = padrão)")]
    public float scale = 1f;

    [Header("Correção de Orientação 3D")]
    [Tooltip("Correção fixa de rotação (X, Y, Z) em graus para alinhar este modelo ao marcador ArUco")]
    public Vector3 rotacaoCorrecao = Vector3.zero;
}