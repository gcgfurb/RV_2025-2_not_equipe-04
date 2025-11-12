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
}