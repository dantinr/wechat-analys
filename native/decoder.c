// SPDX-License-Identifier: GPL-3.0-or-later
#include <stdlib.h>
#include <string.h>
#include "SKP_Silk_SDK_API.h"

// Decode local bytes directly; no filenames, processes or network are involved.
// The caller owns output and supplies its capacity in mono 16-bit samples.
__declspec(dllexport) int wn_silk_decode(const unsigned char *input, int length,
    short *output, int capacity, int *written)
{
    const char header[] = "#!SILK_V3";
    int offset = 0, state_size = 0, result = -1, packets = 0;
    void *state = NULL;
    SKP_SILK_SDK_DecControlStruct control = {0};
    short frame[960];
    if (!written) return -1;
    *written = 0;
    if (!input || !output || length < 9 || capacity <= 0) return -1;
    if (input[0] == 2) offset = 1;
    if (length - offset < 9 || memcmp(input + offset, header, 9) != 0) return -1;
    offset += 9;
    if (SKP_Silk_SDK_Get_Decoder_Size(&state_size) != 0 || state_size <= 0) return -1;
    state = calloc(1, (size_t)state_size);
    if (!state) return -1;
    if (SKP_Silk_SDK_InitDecoder(state) != 0) goto done;
    control.API_sampleRate = 24000;
    control.framesPerPacket = 1;
    while (offset < length)
    {
        int bytes, frames = 0;
        if (length - offset < 2) goto done;
        bytes = input[offset] | (input[offset + 1] << 8);
        offset += 2;
        if (bytes == 65535)
        {
            if (offset != length) goto done;
            break;
        }
        if (bytes > 1024 || bytes > length - offset) goto done;
        do
        {
            short count = 960;
            int status = SKP_Silk_SDK_Decode(state, &control, bytes == 0 ? 1 : 0,
                input + offset, bytes, frame, &count);
            if (status != 0 || count <= 0 || count > 960 || ++frames > 5 || count > capacity - *written) goto done;
            memcpy(output + *written, frame, (size_t)count * sizeof(short));
            *written += count;
        } while (control.moreInternalDecoderFrames);
        offset += bytes;
        packets++;
    }
    if (packets > 0 && *written > 0) result = 0;
done:
    if (result != 0) *written = 0;
    if (state)
    {
        volatile unsigned char *p = (volatile unsigned char *)state;
        int i;
        for (i = 0; i < state_size; i++) p[i] = 0;
        free(state);
    }
    memset(frame, 0, sizeof(frame));
    return result;
}
