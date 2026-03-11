# CUDA / cuDNN runtime DLLs for ONNX Runtime GPU (Windows x64)

Place the required native libraries here so the App and CLI build can copy them to `runtimes\win-x64\native` for the ONNX Runtime CUDA 13 provider. Redistribution is allowed under the [CUDA EULA](https://docs.nvidia.com/cuda/eula/) and [cuDNN license](https://docs.nvidia.com/deeplearning/cudnn/latest/reference/eula.html) when incorporated into your application.

Official dependency list (see [ONNX Runtime CUDA EP](https://onnxruntime.ai/docs/execution-providers/CUDA-ExecutionProvider.html)): **libcudart**, **libcufft**, **libcurand**, **libcublasLt**, **libcublas**, **libcudnn**.

## Full list – place in this folder

### From CUDA Toolkit 13.1.x (`CUDA_PATH\bin\x64`)

| DLL | Library |
|-----|---------|
| `cudart64_13.dll` | CUDA runtime |
| `cufft64_13.dll` | CUDA FFT |
| `curand64_13.dll` | CUDA random number |
| `cublasLt64_13.dll` | cuBLAS (lightweight) |
| `cublas64_13.dll` | cuBLAS |

Copy every `*.dll` from `CUDA_PATH\bin\x64` if you prefer (covers optional deps like `nvrtc64_13*.dll` if needed).

### From cuDNN 9 for CUDA 13 (cuDNN package `bin` or `bin\x64`)

In cuDNN 9 the API is split across multiple DLLs. **`cudnnCreate`** (required by ONNX Runtime) lives in the **graph** library, so you must include all cuDNN DLLs, not just the main one.

| DLL | Notes |
|-----|--------|
| `cudnn64_9.dll` | Main/shim library |
| **`cudnn_graph64_9.dll`** | **Required – contains `cudnnCreate`** (avoids "Cannot load symbol cudnnCreate") |
| `cudnn_ops_infer64_9.dll` | Inference ops |
| `cudnn_cnn_infer64_9.dll` | CNN inference |
| `cudnn_adv_infer64_9.dll` | If present |
| Any other `cudnn*.dll` in the package | Engines, heuristics, etc. |

**Important:** Copy **every** `cudnn*.dll` from the cuDNN package `bin` (or `bin\x64`) folder. Missing the graph or engine DLLs causes "Invalid handle. Cannot load symbol cudnnCreate".

## How to obtain

1. **CUDA**: Install [CUDA Toolkit 13.1.x](https://developer.nvidia.com/cuda-downloads) or use the [redistributable packages](https://developer.download.nvidia.com/compute/cuda/redist/). Copy the DLLs above from the install `bin\x64` folder.
2. **cuDNN**: Download [cuDNN for CUDA 13](https://developer.nvidia.com/cudnn). Copy all `cudnn*.dll` from the package `bin` or `bin\x64` folder.

Any `*.dll` in this folder is copied to the App/CLI output `runtimes\win-x64\native` during build.
