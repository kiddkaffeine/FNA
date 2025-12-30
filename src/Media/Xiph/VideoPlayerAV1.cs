using System;
using System.IO;
using System.IO.MemoryMappedFiles;
using Microsoft.Xna.Framework.Graphics;

namespace Microsoft.Xna.Framework.Media
{
	public unsafe class VideoPlayerAV1 : BaseYUVPlayer
	{
		#region Internal Variables

		private MemoryMappedFile MappedFile { get; set; }
		private MemoryMappedViewAccessor MappedView { get; set; }
		private MemoryMappedViewStream MappedViewStream { get; set; }
		private UnmanagedMemoryStream UnmanagedMemoryStream { get; set; }
		public uint DataSize { get; private set; }
		public void* pData { get; private set; }
		public bool OwnsStream { get; private set; }

		public IntPtr Context { get; private set; }
		private int Width { get; set; } // should we use Video.Width?
		private int Height { get; set; } // should we use Video.Height?
		public Dav1dfile.PixelLayout Layout { get; private set; }
		public int BitsPerPixel { get; private set; }

		#endregion

		#region Public Methods

		public override void Play(Video video)
		{
			throw new System.NotImplementedException(); // TODO
		}

        public void Play(GraphicsDevice graphicsDevice, Stream stream, bool ownsStream)
        {
	        checkDisposed();

            videoTexture = new RenderTargetBinding[1];

            if (stream is FileStream) {
	            FileStream fs = (FileStream)stream;
                // FIXME: Does this inherit the stream position? Does it matter?
                MappedFile = MemoryMappedFile.CreateFromFile(fs, null, 0, MemoryMappedFileAccess.Read, new MemoryMappedFileSecurity(), HandleInheritability.None, !ownsStream);
                MappedView = MappedFile.CreateViewAccessor(0, fs.Length, MemoryMappedFileAccess.Read);
                byte* _pData = null;
                MappedView.SafeMemoryMappedViewHandle.AcquirePointer(ref _pData);
                pData = _pData;
                DataSize = (uint)fs.Length;
            } else if (stream is MemoryMappedViewStream) {
	            MemoryMappedViewStream mmvs = (MemoryMappedViewStream)stream;
                OwnsStream = ownsStream;
                MappedViewStream = mmvs;
                byte* _pData = null;
                mmvs.SafeMemoryMappedViewHandle.AcquirePointer(ref _pData);
                pData = _pData;
                DataSize = (uint)mmvs.Length;
            } else if (stream is UnmanagedMemoryStream) {
	            UnmanagedMemoryStream ums = (UnmanagedMemoryStream)stream;
                OwnsStream = ownsStream;
                UnmanagedMemoryStream = ums;
                pData = ums.PositionPointer;
                DataSize = (uint)(ums.Length - ums.Position);
            } else {
                throw new ArgumentException("Provided stream must be a FileStream, UnmanagedMemoryStream or MemoryMappedViewStream");
            }

            IntPtr context;
            var ok = Dav1dfile.df_open_from_memory((IntPtr)pData, DataSize, out context);
            if (ok == 0)
                throw new Exception("Failed to open video");

            Context = context;

            int width, height;
            Dav1dfile.PixelLayout layout;
            try
            {
	            byte hbd;
                Dav1dfile.df_videoinfo2(context, out width, out height, out layout, out hbd);
                //BitsPerPixel = hbd switch {
                //    2 => 12,
                //    1 => 10,
                //    _ => 8,
                //};
                if (hbd == 2)
                {
	                BitsPerPixel = 12;
                }
                else if (hbd == 1)
                {
	                BitsPerPixel = 10;
                }
                else
                {
	                BitsPerPixel = 8;
                }
            } catch {
                Dav1dfile.df_videoinfo(context, out width, out height, out layout);
                BitsPerPixel = 8;
            }
            Width = width;
            Height = height;
            Layout = layout;

            int uvWidth, uvHeight;

            switch (layout) {
                case Dav1dfile.PixelLayout.I420:
				    uvWidth = Width / 2;
				    uvHeight = Height / 2;
                    break;
                case Dav1dfile.PixelLayout.I422:
				    uvWidth = Width / 2;
                    uvHeight = Height;
                    break;
                case Dav1dfile.PixelLayout.I444:
				    uvWidth = width;
				    uvHeight = height;
                    break;
                default:
                    throw new Exception("Unsupported pixel layout in AV1 file");
            }

            // The VideoPlayer will use the GraphicsDevice that is set now.
            if (currentDevice != graphicsDevice) // TODO: use one from Video
            {
	            GL_dispose();
	            currentDevice = graphicsDevice; // TODO: use one from Video
	            GL_initialize(Resources.YUVToRGBAEffectR);
            }

            RenderTargetBinding overlap = videoTexture[0];
            videoTexture[0] = new RenderTargetBinding(
	            new RenderTarget2D(
		            currentDevice,
		            Width,
		            Height,
		            false,
		            SurfaceFormat.Color,
		            DepthFormat.None,
		            0,
		            RenderTargetUsage.PreserveContents
	            )
            );
            if (overlap.RenderTarget != null)
            {
	            overlap.RenderTarget.Dispose();
            }
            GL_setupTextures(
	            Width,
	            Height,
	            uvWidth,
	            uvHeight,
	            BitsPerPixel > 8 ? SurfaceFormat.UShortEXT : SurfaceFormat.ByteEXT
            );

	        // The player can finally start now!
		    timer.Start();
        }

        public override void Stop()
        {
	        // TODO
        }

        public override void Pause()
        {
	        // TODO
        }

        public override void Resume()
        {
	        // TODO
        }

        public override Texture2D GetTexture()
        {
			if (DecodeAndUpdateFrame(1))
			{
				float rescaleFactor;
				if (BitsPerPixel == 12)
					rescaleFactor = (float)(1.0 / (4096 / 65536.0));
				else if (BitsPerPixel == 10)
					rescaleFactor = (float)(1.0 / (1024 / 65536.0));
				else
					rescaleFactor = 1.0f;

				shaderProgram.Parameters["RescaleFactor"]
					.SetValue(new Vector4(rescaleFactor, rescaleFactor, rescaleFactor, 1.0f));

				// Draw the YUV textures to the framebuffer with our shader.
				GL_pushState();
				currentDevice.DrawPrimitives(
					PrimitiveType.TriangleStrip,
					0,
					2
				);
				GL_popState();
			}

			return videoTexture[0].RenderTarget as Texture2D;
		}

        #endregion

        #region AV1 Internal Methods

        private bool DecodeAndUpdateFrame(int frameCount = 1)
		{
			IntPtr YData, UData, VData;
			uint YLength, UVLength;
			uint YStride, UVStride;
			byte[] YScratchBuffer = null, UVScratchBuffer = null;

			var ok = Dav1dfile.df_readvideo(
				Context, frameCount,
				out YData, out UData, out VData,
				out YLength, out UVLength,
				out YStride, out UVStride
			);

			if (ok != 1)
				return false;

			UploadDataToTexture(yuvTextures[0], YData, YLength, YStride, ref YScratchBuffer);
			UploadDataToTexture(yuvTextures[1], UData, UVLength, UVStride, ref UVScratchBuffer);
			UploadDataToTexture(yuvTextures[2], VData, UVLength, UVStride, ref UVScratchBuffer);

			return true;
		}

		private void UploadDataToTexture(Texture2D texture, IntPtr data, uint length, uint stride, ref byte[] scratchBuffer)
		{
			int w = texture.Width, h = texture.Height,
				dataH = (int)(length / stride),
				availH = Math.Min(dataH, h),
				eltSize = BitsPerPixel > 8 ? 2 : 1,
				rowSize = eltSize * w;

			if (w == stride) {
				texture.SetDataPointerEXT(0, new Rectangle(0, 0, w, availH), data, (int)length);
				return;
			}

			Array.Resize(ref scratchBuffer, w * availH * eltSize);

			fixed (byte* scratch = scratchBuffer) {
				byte* source = (byte*)data;
				/*
				if (TenBit) {
				    // HACK: Rescale to 8 bits
				    unchecked {
				        for (int y = 0; y < availH; y++) {
				            ushort* pSource = (ushort*)(source + (stride * y));
				            byte* pDest = scratch + (w * y);
				            for (int x = 0; x < w; x++) {
				                pDest[x] = (byte)(pSource[x] >> 2);
				            }
				        }
				    }
				} else {
				*/
				for (int y = 0; y < availH; y++) {
					// TODO why is this not available?
					//Buffer.MemoryCopy(source + (stride * y), scratch + (rowSize * y), rowSize, rowSize);
					byte* src = source + (stride * y);
					byte* dst = scratch + (rowSize * y);
					for (int i = 0; i < rowSize; i++)
						*dst++ = *src++;
				}
				// }
				texture.SetDataPointerEXT(0, null, (IntPtr)scratch, scratchBuffer.Length);
			}
		}

		#endregion
	}
}
