"""Execute the offline notebook labs; compile optional live cells without calling them.

Requires nbformat, nbclient, jupyter_client and a registered .NET Interactive C# kernel.
Run from any directory: python3 eng/validate-notebooks.py [--kernel .net-csharp]
No service credentials or model calls are needed. Executed outputs stay in memory.
"""
import argparse
from pathlib import Path

import nbformat
from nbclient import NotebookClient


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--kernel', default='.net-csharp')
    args = parser.parse_args()
    folder = Path(__file__).resolve().parents[1] / 'notebooks' / 'csharp'
    for path in sorted(folder.glob('*.ipynb')):
        nbformat.validate(nbformat.read(path, as_version=4))
    for prefix in ('06-', '07-', '08-'):
        path, = folder.glob(prefix + '*.ipynb')
        notebook = nbformat.read(path, as_version=4)
        for index, cell in enumerate(notebook.cells):
            if cell.cell_type == 'code':
                assert cell.execution_count is None and not cell.outputs, f'Clear saved outputs: {path.name}'
                if 'live-optional' in cell.metadata.get('tags', []):
                    cell.source = f'async Task CompileOptionalLiveCell{index}Async()\n{{\n{cell.source}\n}}'
        NotebookClient(notebook, timeout=180, kernel_name=args.kernel,
                       resources={'metadata': {'path': str(folder)}}).execute()
        print(f'{path.name}: offline assertions passed; optional live cells compiled, not invoked.', flush=True)


if __name__ == '__main__':
    main()
