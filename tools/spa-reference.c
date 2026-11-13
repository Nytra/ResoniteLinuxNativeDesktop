// Prints PipeWire/SPA struct layouts and reference SPA pods built with the real
// spa_pod_builder, so the C# SpaPod builder and interop structs can be checked
// against them (see LinuxNativeDesktop.Tests/SpaPodTests.cs).
//
// Build and run: gcc tools/spa-reference.c $(pkg-config --cflags libpipewire-0.3) -o /tmp/spa-ref && /tmp/spa-ref
#include <stdio.h>
#include <stddef.h>
#include <spa/param/video/format-utils.h>
#include <spa/param/buffers.h>
#include <spa/buffer/buffer.h>
#include <spa/pod/builder.h>
#include <spa/utils/hook.h>
#include <pipewire/stream.h>

static void dump(const char *name, const struct spa_pod *pod)
{
	const uint8_t *p = (const uint8_t *)pod;
	printf("%s (%u bytes):\n", name, (unsigned)SPA_POD_SIZE(pod));
	for (uint32_t i = 0; i < SPA_POD_SIZE(pod); i++)
		printf("0x%02x,%s", p[i], (i % 16 == 15) ? "\n" : " ");
	printf("\n");
}

int main(void)
{
	printf("spa_buffer: n_datas@%zu datas@%zu\n", offsetof(struct spa_buffer, n_datas), offsetof(struct spa_buffer, datas));
	printf("spa_data: type@%zu maxsize@%zu data@%zu chunk@%zu size=%zu\n", offsetof(struct spa_data, type),
	       offsetof(struct spa_data, maxsize), offsetof(struct spa_data, data), offsetof(struct spa_data, chunk), sizeof(struct spa_data));
	printf("spa_chunk: offset@%zu size@%zu stride@%zu flags@%zu\n", offsetof(struct spa_chunk, offset),
	       offsetof(struct spa_chunk, size), offsetof(struct spa_chunk, stride), offsetof(struct spa_chunk, flags));
	printf("pw_stream_events: size=%zu param_changed@%zu process@%zu state_changed@%zu\n", sizeof(struct pw_stream_events),
	       offsetof(struct pw_stream_events, param_changed), offsetof(struct pw_stream_events, process), offsetof(struct pw_stream_events, state_changed));
	printf("spa_hook: size=%zu\n", sizeof(struct spa_hook));
	printf("TYPE_OBJECT_Format=0x%x ParamBuffers=0x%x PARAM_EnumFormat=%d Format=%d Buffers=%d BUFFERS_dataType=%d\n",
	       SPA_TYPE_OBJECT_Format, SPA_TYPE_OBJECT_ParamBuffers, SPA_PARAM_EnumFormat, SPA_PARAM_Format, SPA_PARAM_Buffers, SPA_PARAM_BUFFERS_dataType);
	printf("VIDEO RGBx=%d BGRx=%d RGBA=%d BGRA=%d\n", SPA_VIDEO_FORMAT_RGBx, SPA_VIDEO_FORMAT_BGRx, SPA_VIDEO_FORMAT_RGBA, SPA_VIDEO_FORMAT_BGRA);

	uint8_t buf[1024];
	struct spa_pod_builder b = SPA_POD_BUILDER_INIT(buf, sizeof(buf));
	struct spa_rectangle defSize = SPA_RECTANGLE(1920, 1080), minSize = SPA_RECTANGLE(1, 1), maxSize = SPA_RECTANGLE(16384, 16384);
	struct spa_fraction defRate = SPA_FRACTION(30, 1), minRate = SPA_FRACTION(0, 1), maxRate = SPA_FRACTION(30, 1);
	const struct spa_pod *enumFormat = spa_pod_builder_add_object(&b,
		SPA_TYPE_OBJECT_Format, SPA_PARAM_EnumFormat,
		SPA_FORMAT_mediaType, SPA_POD_Id(SPA_MEDIA_TYPE_video),
		SPA_FORMAT_mediaSubtype, SPA_POD_Id(SPA_MEDIA_SUBTYPE_raw),
		SPA_FORMAT_VIDEO_format, SPA_POD_CHOICE_ENUM_Id(5, SPA_VIDEO_FORMAT_RGBx, SPA_VIDEO_FORMAT_RGBx,
			SPA_VIDEO_FORMAT_RGBA, SPA_VIDEO_FORMAT_BGRx, SPA_VIDEO_FORMAT_BGRA),
		SPA_FORMAT_VIDEO_size, SPA_POD_CHOICE_RANGE_Rectangle(&defSize, &minSize, &maxSize),
		SPA_FORMAT_VIDEO_framerate, SPA_POD_CHOICE_RANGE_Fraction(&defRate, &minRate, &maxRate));
	dump("EnumFormat", enumFormat);

	b = (struct spa_pod_builder)SPA_POD_BUILDER_INIT(buf, sizeof(buf));
	const struct spa_pod *buffers = spa_pod_builder_add_object(&b,
		SPA_TYPE_OBJECT_ParamBuffers, SPA_PARAM_Buffers,
		SPA_PARAM_BUFFERS_dataType, SPA_POD_CHOICE_FLAGS_Int((1 << SPA_DATA_MemPtr) | (1 << SPA_DATA_MemFd)));
	dump("Buffers", buffers);

	b = (struct spa_pod_builder)SPA_POD_BUILDER_INIT(buf, sizeof(buf));
	struct spa_video_info_raw info = { .format = SPA_VIDEO_FORMAT_BGRx, .size = SPA_RECTANGLE(2560, 1440), .framerate = SPA_FRACTION(0, 1) };
	const struct spa_pod *format = spa_format_video_raw_build(&b, SPA_PARAM_Format, &info);
	dump("Format", format);
	return 0;
}
