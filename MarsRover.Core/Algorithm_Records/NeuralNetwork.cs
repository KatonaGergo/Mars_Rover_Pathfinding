using System.Text.Json;

namespace MarsRover.Core.Algorithm;

/// <summary>
/// Fully-connected neural network for DQN Q-function approximation.
///
/// Architecture: [inputSize] → [hidden1] → [hidden2] → [outputSize]
/// Activation: ReLU on hidden layers, linear on output (Q-values can be negative)
///
/// This implementation is intentionally lightweight (no ML.NET / TensorFlow):
///   - no extra NuGet dependency, so the project stays portable
///   - full control over initialization, updates, and target-net cloning
///   - the network is small enough that a custom implementation is practical
///
/// Weight initialization uses He init (var = 2/fan_in).
/// Training uses mini-batch gradient descent with MSE on Q-values.
/// </summary>
public class NeuralNetwork
{
    // Architecture
    public  int InputSize  { get; }
    public  int OutputSize { get; }
    private readonly int[] _layerSizes; // [input, hidden1, hidden2, output]

    // Weights and biases  [layer][neuron_out][neuron_in]
    private double[][][] _weights; // _weights[l][j][i] = w from i→j in layer l
    private double[][]   _biases;  // _biases[l][j]

    // Hyperparameters
    public double LearningRate { get; set; } = 0.001;

    private readonly Random _rng;



    public NeuralNetwork(int inputSize, int hidden1, int hidden2, int outputSize, int seed = 42)
    {
        InputSize   = inputSize;
        OutputSize  = outputSize;
        _layerSizes = [inputSize, hidden1, hidden2, outputSize];
        _rng        = new Random(seed);

        InitWeights();
    }

    // Private constructor used by Clone()
    private NeuralNetwork(int[] layerSizes, double[][][] weights, double[][] biases)
    {
        _layerSizes = layerSizes;
        InputSize   = layerSizes[0];
        OutputSize  = layerSizes[^1];
        _weights    = weights;
        _biases     = biases;
        _rng        = new Random(0);
    }

    // Initialisation

    private void InitWeights()
    {
        int layers = _layerSizes.Length - 1; // number of weight layers
        _weights   = new double[layers][][];
        _biases    = new double[layers][];

        for (int l = 0; l < layers; l++)
        {
            int fanIn  = _layerSizes[l];
            int fanOut = _layerSizes[l + 1];
            double std = Math.Sqrt(2.0 / fanIn); // He init

            _weights[l] = new double[fanOut][];
            _biases[l]  = new double[fanOut];

            for (int j = 0; j < fanOut; j++)
            {
                _weights[l][j] = new double[fanIn];
                for (int i = 0; i < fanIn; i++)
                    _weights[l][j][i] = SampleGaussian() * std;
                _biases[l][j] = 0.0; // biases start at 0
            }
        }
    }

    // Forward pass

    /// <summary>
    /// Runs a forward pass and returns Q-values for all actions.
    /// </summary>
    public double[] Forward(double[] input)
    {
        var activation = input;

        for (int l = 0; l < _weights.Length; l++)
        {
            int fanOut = _weights[l].Length;
            var next   = new double[fanOut];

            for (int j = 0; j < fanOut; j++)
            {
                double sum = _biases[l][j];
                for (int i = 0; i < activation.Length; i++)
                    sum += _weights[l][j][i] * activation[i];

                // ReLU on hidden layers, linear on final layer
                next[j] = (l < _weights.Length - 1) ? Math.Max(0, sum) : sum;
            }

            activation = next;
        }

        return activation; // Q-values
    }

    // Backward pass (mini-batch SGD)

    /// <summary>
    /// Updates weights given a batch of (input, targetQValues) pairs.
    /// Only the Q-value at actionIndex is updated — others are left untouched
    /// (standard DQN selective target: don't regress on actions not taken).
    /// </summary>
    public void Train(double[][] inputs, double[][] targets, int[] actionIndices)
    {
        int layers = _weights.Length;
        int batch  = inputs.Length;

        // Accumulate gradients across the batch
        var dW = new double[layers][][];
        var dB = new double[layers][];

        for (int l = 0; l < layers; l++)
        {
            int fanOut = _weights[l].Length;
            int fanIn  = _weights[l][0].Length;
            dW[l] = new double[fanOut][];
            dB[l] = new double[fanOut];
            for (int j = 0; j < fanOut; j++)
                dW[l][j] = new double[fanIn];
        }

        for (int b = 0; b < batch; b++)
        {
            // Forward pass, storing activations and pre-activations
            var activations = new double[layers + 1][];
            var preActs     = new double[layers][];
            activations[0]  = inputs[b];

            for (int l = 0; l < layers; l++)
            {
                int fanOut  = _weights[l].Length;
                preActs[l]  = new double[fanOut];
                activations[l + 1] = new double[fanOut];

                for (int j = 0; j < fanOut; j++)
                {
                    double sum = _biases[l][j];
                    for (int i = 0; i < activations[l].Length; i++)
                        sum += _weights[l][j][i] * activations[l][i];
                    preActs[l][j]      = sum;
                    activations[l + 1][j] = (l < layers - 1) ? Math.Max(0, sum) : sum;
                }
            }

            // Output layer delta: MSE loss, only on the action taken
            int    actionIdx = actionIndices[b];
            var    delta     = new double[layers][];
            delta[layers-1]  = new double[_weights[layers-1].Length];

            for (int j = 0; j < delta[layers-1].Length; j++)
            {
                // Only backprop on the action we took — others contribute 0 gradient
                if (j == actionIdx)
                    delta[layers-1][j] = activations[layers][j] - targets[b][j]; // dMSE/dQ
                // else stays 0
            }

            // Hidden layer deltas (backprop through ReLU)
            for (int l = layers - 2; l >= 0; l--)
            {
                delta[l] = new double[_weights[l].Length];

                for (int i = 0; i < delta[l].Length; i++)
                {
                    if (preActs[l][i] <= 0) continue; // ReLU gate

                    double grad = 0;
                    for (int j = 0; j < delta[l+1].Length; j++)
                        grad += _weights[l+1][j][i] * delta[l+1][j];
                    delta[l][i] = grad;
                }
            }

            // Accumulate gradients
            for (int l = 0; l < layers; l++)
            {
                for (int j = 0; j < _weights[l].Length; j++)
                {
                    dB[l][j] += delta[l][j];
                    for (int i = 0; i < activations[l].Length; i++)
                        dW[l][j][i] += delta[l][j] * activations[l][i];
                }
            }
        }

        // Apply averaged gradient update
        double scale = LearningRate / batch;
        for (int l = 0; l < layers; l++)
            for (int j = 0; j < _weights[l].Length; j++)
            {
                _biases[l][j] -= scale * dB[l][j];
                for (int i = 0; i < _weights[l][j].Length; i++)
                    _weights[l][j][i] -= scale * dW[l][j][i];
            }
    }

    // Target network support

    /// <summary>
    /// Returns a deep copy of this network — used to create the frozen target net.
    /// </summary>
    public NeuralNetwork Clone()
    {
        int layers = _weights.Length;
        var w = new double[layers][][];
        var b = new double[layers][];

        for (int l = 0; l < layers; l++)
        {
            int fanOut = _weights[l].Length;
            w[l] = new double[fanOut][];
            b[l] = new double[fanOut];
            for (int j = 0; j < fanOut; j++)
            {
                w[l][j] = (double[])_weights[l][j].Clone();
                b[l][j] = _biases[l][j];
            }
        }

        return new NeuralNetwork((int[])_layerSizes.Clone(), w, b);
    }

    /// <summary>
    /// Copies weights FROM another network INTO this one (used to sync target net).
    /// </summary>
    public void CopyWeightsFrom(NeuralNetwork source)
    {
        for (int l = 0; l < _weights.Length; l++)
            for (int j = 0; j < _weights[l].Length; j++)
            {
                Array.Copy(source._weights[l][j], _weights[l][j], _weights[l][j].Length);
                _biases[l][j] = source._biases[l][j];
            }
    }

    // Serialisation

    public void Save(string path)
    {
        var dto = new NetworkDto
        {
            LayerSizes = _layerSizes,
            Weights    = _weights,
            Biases     = _biases
        };
        File.WriteAllText(path, JsonSerializer.Serialize(dto,
            new JsonSerializerOptions { WriteIndented = false }));
    }

    public static NeuralNetwork Load(string path)
    {
        var dto = JsonSerializer.Deserialize<NetworkDto>(File.ReadAllText(path))
            ?? throw new InvalidDataException("Could not parse network weights file.");
        return new NeuralNetwork(dto.LayerSizes!, dto.Weights!, dto.Biases!);
    }

    public bool FileExists(string path) => File.Exists(path);

    // Helpers

    private double SampleGaussian()
    {
        // Box-Muller transform
        double u1 = 1.0 - _rng.NextDouble();
        double u2 = 1.0 - _rng.NextDouble();
        return Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Sin(2.0 * Math.PI * u2);
    }

    // DTO for JSON serialisation
    private class NetworkDto
    {
        public int[]?       LayerSizes { get; set; }
        public double[][][]? Weights   { get; set; }
        public double[][]?   Biases    { get; set; }
    }
}
