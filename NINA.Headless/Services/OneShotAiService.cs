using System;
using System.IO;
using System.Linq;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace NINA.Headless.Services
{
    public class OneShotAiService : IDisposable
    {
        private InferenceSession? _session;

        public OneShotAiService()
        {
            try
            {
                string modelPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "MLModels", "oneshot_autofocus.onnx");
                if (File.Exists(modelPath))
                {
                    _session = new InferenceSession(modelPath);
                }
            }
            catch
            {
                // Silently bypass if model not yet uploaded or supported
            }
        }

        public bool IsModelLoaded => _session != null;

        public float PredictOffset(string imagePath)
        {
            if (!IsModelLoaded || !File.Exists(imagePath))
            {
                // Return dummy prediction for testing the pipeline if model is missing
                // Random positive/negative offset to bounce around the focus point for test visualization
                return (new Random().NextSingle() * 100) - 50;
            }

            try
            {
                // 1. Load Image using ImageSharp
                using SixLabors.ImageSharp.Image<Rgb24> image = SixLabors.ImageSharp.Image.Load<Rgb24>(imagePath);
                
                // 2. Preprocess: Resize to 64x64, Grayscale
                image.Mutate(x => x.Resize(64, 64).Grayscale());

                // 3. Convert to Tensor float[1, 1, 64, 64]
                var tensor = new DenseTensor<float>(new[] { 1, 1, 64, 64 });
                for (int y = 0; y < image.Height; y++)
                {
                    for (int x = 0; x < image.Width; x++)
                    {
                        var pixel = image[x, y];
                        // Normalize pixel value to 0-1
                        tensor[0, 0, y, x] = pixel.R / 255.0f;
                    }
                }

                // 4. Run Inference
                var inputs = new NamedOnnxValue[]
                {
                    NamedOnnxValue.CreateFromTensor("input", tensor)
                };

                using var results = _session.Run(inputs);
                
                // 5. Extract Output (assuming output is a single float value: absolute distance)
                var output = results.First().AsTensor<float>();
                return output[0];
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[OneShotAiService] Inference error: {ex.Message}");
                return 42.0f; // Dummy fallback
            }
        }

        public void Dispose()
        {
            if (_session != null)
            {
                _session.Dispose();
            }
        }
    }
}
