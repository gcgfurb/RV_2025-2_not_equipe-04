using UnityEngine;

public class GameManager : MonoBehaviour
{
    public static GameManager Instance;
    public Dificuldade dificuldadeAtual;

    private void Awake()
    {
        if (Instance == null)
        {
            Instance = this;
            DontDestroyOnLoad(gameObject);
        }
        else
        {
            Destroy(gameObject);
        }
    }

    public void DefinirDificuldade(Dificuldade dificuldade)
    {
        dificuldadeAtual = dificuldade;
        Debug.Log("Dificuldade definida: " + dificuldade);
    }
}
