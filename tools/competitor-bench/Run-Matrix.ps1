#requires -Version 5.1
<# Same-model/same-prompts engine matrix: each product's engine with its own default flags and with PaRuX's flags.
   Paths are parameters so the harness carries no machine-specific data. #>
param(
  [Parameter(Mandatory)][string]$Model,
  [Parameter(Mandatory)][string]$ParuxCuda,      # PaRuX-managed llama.cpp CUDA llama-server.exe
  [Parameter(Mandatory)][string]$OutDir,
  [string]$ParuxCudaNewer,                       # optional newer build for a build A/B
  [string]$LuServer = 'C:\Program Files\Locally Uncensored\lu-llama-server.exe'
)
$run = Join-Path (Split-Path -Parent $MyInvocation.MyCommand.Path) 'Run-Engine.ps1'
$PX = '--alias local-coder --jinja --reasoning auto --ctx-size 16384 --batch-size 2048 --ubatch-size 1024 --cache-type-k q8_0 --cache-type-v q8_0 --flash-attn on --gpu-layers all --fit on --fit-target 1024 --cont-batching --slot-prompt-similarity 0.10 --load-mode none --parallel 1 --split-mode none --main-gpu 0'
$PXf16 = $PX -replace '--cache-type-k q8_0 --cache-type-v q8_0 ', ''
$PXdefb = $PX -replace '--batch-size 2048 --ubatch-size 1024 ', ''
$LUF = '--ctx-size 32768 -ngl 999 --load-mode none'
Get-Process llama-server, lu-llama-server -ErrorAction SilentlyContinue | Stop-Process -Force
$cfg = @(
  @('m1-px-cuda-pxflags', $ParuxCuda, $PX),
  @('m3-px-cuda-pxflags-kvf16', $ParuxCuda, $PXf16),
  @('m4-px-cuda-pxflags-defbatch', $ParuxCuda, $PXdefb),
  @('m5-px-cuda-luflags', $ParuxCuda, $LUF),
  @('m6-luengine-luflags', $LuServer, $LUF),
  @('m7-luengine-pxflags', $LuServer, $PX),
  @('m8-luengine-luflags-ctx16k', $LuServer, '--ctx-size 16384 -ngl 999 --load-mode none')
)
if ($ParuxCudaNewer) { $cfg += , @('m2-px-cudaNewer-pxflags', $ParuxCudaNewer, $PX) }
foreach ($c in $cfg) { & $run -Label $c[0] -Server $c[1] -Model $Model -ArgLine $c[2] -OutDir $OutDir }
